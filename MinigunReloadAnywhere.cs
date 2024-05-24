using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Minigun Reload Anywhere", "VisEntities", "1.0.0")]
    [Description("Allows reloading the minigun anywhere without needing a workbench.")]
    public class MinigunReloadAnywhere : RustPlugin
    {
        #region Fields

        private static MinigunReloadAnywhere _plugin;
        private static Configuration _config;
        private const string FX_MINIGUN_RELOAD = "assets/prefabs/weapons/minigun/effects/minigun-reload.prefab";
        private Dictionary<BasePlayer, GunReloaderComponent> _gunReloaders = new Dictionary<BasePlayer, GunReloaderComponent>();

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Reload Cooldown Seconds")]
            public float ReloadCooldownSeconds { get; set; }
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

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                ReloadCooldownSeconds = 5f
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

            GunReloaderComponent gunReloader = GunReloaderComponent.InstallComponent(player, item);
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
                    gunReloader.DestroyComponent();
            }

            _gunReloaders.Clear();
        }

        public void DestroyGunReloader(BasePlayer player)
        {
            if (_gunReloaders.TryGetValue(player, out GunReloaderComponent gunReloader) && gunReloader != null)
            {
                gunReloader.DestroyComponent();
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
            #region Fields

            private Item _item;
            private BasePlayer _player;
            private InputState _playerInput;

            private bool _reloadButtonPressed = false;
            private float _reloadCooldownSeconds;
            private float _nextReloadTime = float.NegativeInfinity;
            
            #endregion Fields

            #region Component Management

            public static GunReloaderComponent InstallComponent(BasePlayer player, Item item)
            {
                GunReloaderComponent component = player.gameObject.AddComponent<GunReloaderComponent>();
                component.InitializeComponent(item);
                return component;
            }

            public GunReloaderComponent InitializeComponent(Item item)
            {
                _player = GetComponent<BasePlayer>();
                _playerInput = _player.serverInput;
                _item = item;

                _reloadCooldownSeconds = _config.ReloadCooldownSeconds;
                return this;
            }

            public static GunReloaderComponent GetComponent(BasePlayer player)
            {
                return player.gameObject.GetComponent<GunReloaderComponent>();
            }

            public void DestroyComponent()
            {
                DestroyImmediate(this);
            }

            #endregion Component Management

            #region Component Lifecycle

            private void Update()
            {
                if (_playerInput.WasJustPressed(BUTTON.RELOAD) && !_reloadButtonPressed && !HasReloadCooldown())
                {
                    TryReload();
                    _reloadButtonPressed = true;
                }
                else if (_playerInput.WasJustReleased(BUTTON.RELOAD))
                {
                    _reloadButtonPressed = false;
                }
            }

            private void OnDestroy()
            {
                _plugin.HandleGunReloaderDestroyed(_player);
            }

            #endregion Component Lifecycle

            #region Reloading
            
            private void TryReload()
            {
                BaseEntity heldEntity = _item.GetHeldEntity();
                if (heldEntity == null)
                    return;

                BaseProjectile weapon = heldEntity.GetComponent<BaseProjectile>();
                if (weapon == null)
                    return;
                
                int ammoNeeded = weapon.primaryMagazine.capacity - weapon.primaryMagazine.contents;
                if (ammoNeeded == 0)
                    return;

                if (weapon.TryReloadMagazine(_player.inventory, ammoNeeded))
                {
                    Effect.server.Run(FX_MINIGUN_RELOAD, _player.eyes.position, default, null, false);
                    StartReloadCooldown(_reloadCooldownSeconds);
                }
            }

            private void StartReloadCooldown(float cooldownSeconds)
            {
                _nextReloadTime = Time.realtimeSinceStartup + cooldownSeconds;
            }

            private bool HasReloadCooldown()
            {
                return Time.realtimeSinceStartup < _nextReloadTime;
            }

            #endregion Reloading
        }

        #endregion Gun Reloader Component

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
    }
}