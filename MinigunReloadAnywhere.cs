/*
 * Copyright (C) 2024 Game4Freak.io
 * This mod is provided under the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Network;
using Newtonsoft.Json;
using Oxide.Game.Rust.Cui;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Minigun Reload Anywhere", "VisEntities", "1.4.1")]
    [Description("Allows reloading the minigun anywhere without needing a workbench.")]
    public class MinigunReloadAnywhere : RustPlugin
    {
        #region Fields

        private static MinigunReloadAnywhere _plugin;
        private static Configuration _config;

        private const string FX_RELOAD = "assets/prefabs/weapons/minigun/effects/minigun-reload.prefab";
        private Dictionary<BasePlayer, GunReloaderComponent> _gunReloaders = new Dictionary<BasePlayer, GunReloaderComponent>();

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Reload Duration Seconds")]
            public float ReloadDurationSeconds { get; set; }

            [JsonProperty("Reload Cooldown Seconds")]
            public float ReloadCooldownSeconds { get; set; }

            [JsonProperty("Enable Toast Notifications")]
            public bool EnableToastNotifications { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;

            if (string.Compare(_config.Version, "1.3.0") < 0)
            {
                _config.ReloadDurationSeconds = defaultConfig.ReloadDurationSeconds;
                _config.EnableToastNotifications = defaultConfig.EnableToastNotifications;
            }

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                ReloadCooldownSeconds = 10f,
                ReloadDurationSeconds = 5f,
                EnableToastNotifications = true
            };
        }

        #endregion Configuration

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
            PermissionUtil.RegisterPermissions();
        }

        private void Unload()
        {
            UnloadAllGunReloaders();
            _config = null;
            _plugin = null;
        }

        private void OnActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (player == null)
                return;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE))
                return;

            if (oldItem != null && oldItem.info.shortname.Contains("minigun"))
            {
                DestroyGunReloader(player);
            }

            if (newItem != null && newItem.info.shortname.Contains("minigun"))
            {
                GetOrAddGunReloader(player, newItem);
            }
        }

        #endregion Oxide Hooks

        #region Gun Reloader Management

        public GunReloaderComponent GetOrAddGunReloader(BasePlayer player, Item item)
        {
            if (_gunReloaders.TryGetValue(player, out GunReloaderComponent existingGunReloader))
                return existingGunReloader;

            GunReloaderComponent gunReloader = GunReloaderComponent.Install(player, item);
            _gunReloaders[player] = gunReloader;
            return gunReloader;
        }

        public void HandleGunReloaderDestroyed(BasePlayer player)
        {
            if (_gunReloaders.ContainsKey(player))
                _gunReloaders.Remove(player);
        }
        
        public void UnloadAllGunReloaders()
        {
            foreach (GunReloaderComponent gunReloader in _gunReloaders.Values.ToArray())
            {
                if (gunReloader != null)
                    gunReloader.DestroySelf();
            }

            _gunReloaders.Clear();
        }

        public void DestroyGunReloader(BasePlayer player)
        {
            if (_gunReloaders.TryGetValue(player, out GunReloaderComponent gunReloader) && gunReloader != null)
            {
                ReloadUi.Hide(player);
                gunReloader.DestroySelf();
                _gunReloaders.Remove(player);
            }
        }

        public GunReloaderComponent GetGunReloader(BasePlayer player)
        {
            if (_gunReloaders.TryGetValue(player, out GunReloaderComponent gunReloader))
                return gunReloader;

            return null;
        }

        public bool PlayerHasGunReloader(BasePlayer player)
        {
            return _gunReloaders.ContainsKey(player);
        }

        #endregion Gun Reloader Management

        #region Gun Reloader Component

        public class GunReloaderComponent : FacepunchBehaviour
        {
            private Item _item;
            private BasePlayer _player;
            private InputState _playerInput;

            private bool _buttonHeld;
            private float _heldTime;
            private float _nextReloadTime = float.NegativeInfinity;
            private bool _warnSent;
            private float _holdStart;

            public static GunReloaderComponent Install(BasePlayer player, Item item)
            {
                GunReloaderComponent component = player.gameObject.AddComponent<GunReloaderComponent>();
                component.Initialize(item);
                return component;
            }

            public GunReloaderComponent Initialize(Item item)
            {
                _player = GetComponent<BasePlayer>();
                _playerInput = _player.serverInput;
                _item = item;

                return this;
            }
            
            public static GunReloaderComponent Get(BasePlayer player)
            {
                return player.gameObject.GetComponent<GunReloaderComponent>();
            }

            public void DestroySelf()
            {
                DestroyImmediate(this);
            }

            private void Update()
            {
                Item active = _player.GetActiveItem();
                if (active == null || active.uid != _item.uid)
                {
                    DestroySelf();
                    return;
                }

                if (_playerInput.IsDown(BUTTON.RELOAD) && !_buttonHeld)
                {
                    if (Time.realtimeSinceStartup < _nextReloadTime)
                    {
                        if (!_warnSent)
                        {
                            float remaining = _nextReloadTime - Time.realtimeSinceStartup;
                            string txt = FormatTime(remaining, true);

                            MessagePlayer(_player, Lang.ReloadCooldown, txt);

                            if (_config.EnableToastNotifications)
                                ShowToast(_player, Lang.ReloadCooldown, GameTip.Styles.Blue_Normal, txt);

                            _warnSent = true;
                        }
                        return;
                    }

                    BaseProjectile weapon = _item.GetHeldEntity()?.GetComponent<BaseProjectile>();
                    if (weapon == null) return;

                    int need = weapon.primaryMagazine.capacity - weapon.primaryMagazine.contents;
                    if (need <= 0)
                    {
                        if (!_warnSent)
                        {
                            MessagePlayer(_player, Lang.MagazineFull);

                            if (_config.EnableToastNotifications)
                                ShowToast(_player, Lang.MagazineFull, GameTip.Styles.Blue_Normal);

                            _warnSent = true;
                        }
                        return;
                    }

                    ItemDefinition ammoDef = weapon.primaryMagazine.ammoType;
                    int ammoCount = _player.inventory.GetAmount(ammoDef.itemid);
                    if (ammoCount <= 0)
                    {
                        if (!_warnSent)
                        {
                            MessagePlayer(_player, Lang.NoAmmo);

                            if (_config.EnableToastNotifications)
                                ShowToast(_player, Lang.NoAmmo, GameTip.Styles.Blue_Normal);

                            _warnSent = true;
                        }
                        return;
                    }

                    if (_config.ReloadDurationSeconds <= 0f)
                    {
                        DoReload();
                        _nextReloadTime = Time.realtimeSinceStartup + _config.ReloadCooldownSeconds;
                        return;
                    }

                    _buttonHeld = true;
                    _holdStart = Time.realtimeSinceStartup;
                    ReloadUi.Show(_player, 0f);
                    return;
                }

                if (_buttonHeld && _playerInput.IsDown(BUTTON.RELOAD))
                {
                    float held = Time.realtimeSinceStartup - _holdStart;
                    float ratio = held / _config.ReloadDurationSeconds;
                    ReloadUi.Show(_player, ratio);

                    if (held >= _config.ReloadDurationSeconds)
                    {
                        DoReload();
                        ReloadUi.Hide(_player);
                        _buttonHeld = false;
                        _nextReloadTime = Time.realtimeSinceStartup + _config.ReloadCooldownSeconds;
                    }
                    return;
                }

                if (_buttonHeld && _playerInput.WasJustReleased(BUTTON.RELOAD))
                {
                    _buttonHeld = false;
                    _warnSent = false;
                    ReloadUi.Hide(_player);
                }
            }

            private void OnDestroy()
            {
                ReloadUi.Hide(_player);
                _plugin.HandleGunReloaderDestroyed(_player);
            }

            private void DoReload()
            {
                BaseEntity heldEntity = _item.GetHeldEntity();
                if (heldEntity == null) return;

                BaseProjectile weapon = heldEntity.GetComponent<BaseProjectile>();
                if (weapon == null) return;

                int need = weapon.primaryMagazine.capacity - weapon.primaryMagazine.contents;
                if (need == 0) return;

                if (weapon.TryReloadMagazine(_player.inventory, need))
                    RunEffectAttachedToEntity(FX_RELOAD, _player);
            }
        }

        #endregion Gun Reloader Component

        #region Helper Functions

        public static string FormatTime(float time, bool compactFormat = true)
        {
            int hours = Mathf.FloorToInt(time / 3600f);
            int minutes = Mathf.FloorToInt((time % 3600) / 60f);
            int seconds = Mathf.FloorToInt(time % 60);

            if (compactFormat)
            {
                if (hours > 0)
                {
                    return $"{hours}h {minutes}m";
                }
                else if (minutes > 0)
                {
                    return $"{minutes}m {seconds}s";
                }
                else
                {
                    return $"{seconds}s";
                }
            }
            else
            {
                if (hours > 0)
                {
                    return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
                }
                else if (minutes > 0)
                {
                    return $"{minutes:D2}:{seconds:D2}";
                }
                else
                {
                    return $"{seconds:D2}";
                }
            }
        }

        public static void RunEffectAttachedToEntity(string effectName, BaseEntity attachedEntity, uint entityBoneID = 0u, Vector3 localPosition = default(Vector3),
                Vector3 localNormal = default(Vector3), Connection suppressFor = null, bool sendToAll = false, List<Connection> recipients = null)
        {
            Effect.server.Run(effectName, attachedEntity, entityBoneID, localPosition, localNormal, suppressFor, sendToAll, recipients);
        }

        #endregion Helper Functions

        #region Permissions

        private static class PermissionUtil
        {
            public const string USE = "minigunreloadanywhere.use";
            private static readonly List<string> _permissions = new List<string>
            {
                USE,
            };

            public static void RegisterPermissions()
            {
                foreach (var permission in _permissions)
                {
                    _plugin.permission.RegisterPermission(permission, _plugin);
                }
            }

            public static bool HasPermission(BasePlayer player, string permissionName)
            {
                return _plugin.permission.UserHasPermission(player.UserIDString, permissionName);
            }
        }

        #endregion Permissions

        #region Localization

        private class Lang
        {
            public const string ReloadCooldown = "ReloadCooldown";
            public const string MagazineFull = "MagazineFull";
            public const string NoAmmo = "NoAmmo";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.ReloadCooldown] = "You can reload again in {0} seconds.",
                [Lang.MagazineFull] = "Your minigun is already fully loaded.",
                [Lang.NoAmmo] = "You have no ammo to reload your minigun.",

            }, this, "en");
        }

        private static string GetMessage(BasePlayer player, string messageKey, params object[] args)
        {
            string message = _plugin.lang.GetMessage(messageKey, _plugin, player.UserIDString);

            if (args.Length > 0)
                message = string.Format(message, args);

            return message;
        }

        public static void MessagePlayer(BasePlayer player, string messageKey, params object[] args)
        {
            string message = GetMessage(player, messageKey, args);

            if (!string.IsNullOrWhiteSpace(message))
                _plugin.SendReply(player, message);
        }

        public static void ShowToast(BasePlayer player, string messageKey, GameTip.Styles style = GameTip.Styles.Blue_Normal, params object[] args)
        {
            string message = GetMessage(player, messageKey, args);

            if (!string.IsNullOrWhiteSpace(message))
                player.SendConsoleCommand("gametip.showtoast", (int)style, message);
        }

        #endregion Localization

        #region UI

        public static class ReloadUi
        {
            private const string UI_ROOT = "mra-reload-root";
            private const string UI_BG = "mra-reload-bar-bg";
            private const string UI_FILL = "mra-reload-bar-fill";

            public static void Show(BasePlayer player, float ratio, string header = "Reloading…")
            {
                var ui = new CuiElementContainer();

                ui.Add(new CuiElement
                {
                    Name = UI_ROOT,
                    Parent = "Under",
                    DestroyUi = UI_ROOT,
                    Components =
                    {
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = "-266.6667 -93.3333",
                            OffsetMax = "266.6667 -66.6667"
                        },
                        new CuiTextComponent
                        {
                            Text     = header,
                            Font     = "robotocondensed-bold.ttf",
                            FontSize = 15,
                            Align    = TextAnchor.MiddleCenter,
                            Color    = HexToColor("D2F1FF")
                        },
                        new CuiOutlineComponent
                        {
                            Color    = HexToColor("020E19"),
                            Distance = "1.3 1.3"
                        }
                    }
                });

                ui.Add(new CuiElement
                {
                    Name = UI_BG,
                    Parent = UI_ROOT,
                    DestroyUi = UI_BG,
                    Components =
                    {
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = "-153.3333 -25.5",
                            OffsetMax = "153.3333 -13.5"
                        },
                        new CuiImageComponent { Color = HexToColor("03193A") },
                        new CuiOutlineComponent
                        {
                            Color    = HexToColor("020E19"),
                            Distance = "2.0 2.0"
                        }
                    }
                });

                const float left = -152f;
                const float right = 152f;
                float fillRight = Mathf.Lerp(left, right, Mathf.Clamp01(ratio));

                ui.Add(new CuiElement
                {
                    Name = UI_FILL,
                    Parent = UI_BG,
                    DestroyUi = UI_FILL,
                    Components =
                    {
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{left} -4.333333",
                            OffsetMax = $"{fillRight} 4.833333"
                        },
                        new CuiImageComponent { Color = HexToColor("1192D2") }
                    }
                });

                CuiHelper.AddUi(player, ui);
            }

            public static void Hide(BasePlayer player)
            {
                CuiHelper.DestroyUi(player, UI_ROOT);
            }

            private static string HexToColor(string hex, float opacity = 100f)
            {
                if (string.IsNullOrEmpty(hex) || hex.Length < 6) hex = "FFFFFF";
                hex = hex.TrimStart('#');

                int r = int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                int g = int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                int b = int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);

                return $"{r / 255f:F3} {g / 255f:F3} {b / 255f:F3} {opacity / 100f:F3}";
            }
        }

        #endregion UI
    }
}