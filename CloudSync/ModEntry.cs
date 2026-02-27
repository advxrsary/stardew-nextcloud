using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using CloudSync.Integration;
using CloudSync.Patches;
using CloudSync.Sync;
using CloudSync.Utils;
using CloudSync.WebDav;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace CloudSync
{
    internal class ModEntry : Mod
    {
        private ModConfig Config = null!;
        private WebDavClient? Client;
        private SyncManager? Sync;
        private CloudSaveChecker? CloudChecker;
        private bool Enabled;
        private string LastInitKey = "";
        private readonly ConcurrentQueue<string> HudQueue = new();

        public override void Entry(IModHelper helper)
        {
            this.Config = helper.ReadConfig<ModConfig>();

            // Always register events so GMCM can configure even without credentials
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.Saved += OnSaved;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            helper.ConsoleCommands.Add(
                "cloudsync",
                "CloudSync commands: status, sync, restore, test, list",
                OnCommand
            );

            InitSyncClient();
        }

        /// <summary>Initialize or reinitialize the WebDAV client and sync manager.</summary>
        private void InitSyncClient()
        {
            if (string.IsNullOrWhiteSpace(this.Config.NextcloudUrl) ||
                string.IsNullOrWhiteSpace(this.Config.Username) ||
                string.IsNullOrWhiteSpace(this.Config.Password))
            {
                if (this.Enabled || this.LastInitKey == "")
                {
                    this.Monitor.Log(
                        "CloudSync: configure NextcloudUrl, Username, and Password " +
                        "to enable sync. Use GMCM or edit config.json.",
                        LogLevel.Warn
                    );
                }
                this.Enabled = false;
                this.Client = null;
                this.Sync = null;
                this.CloudChecker = null;
                this.LastInitKey = "";
                return;
            }

            // Skip reinit if connection params haven't changed
            string initKey = $"{this.Config.NextcloudUrl}|{this.Config.Username}|{this.Config.Password}|{this.Config.TimeoutSeconds}";
            if (initKey == this.LastInitKey && this.Enabled)
                return;

            this.Client = new WebDavClient(
                this.Config.NextcloudUrl,
                this.Config.Username,
                this.Config.Password,
                this.Config.TimeoutSeconds,
                this.Monitor
            );

            var state = new SyncState(this.Helper.DirectoryPath, this.Monitor);
            var conflicts = new ConflictResolver(this.Config.MaxBackups, this.Monitor);
            var paths = new PathHelper(this.Helper);

            this.Sync = new SyncManager(
                this.Client, state, conflicts, paths, this.Config, this.Monitor
            );

            this.CloudChecker = new CloudSaveChecker(
                this.Client, this.Config, this.Monitor
            );

            this.Enabled = true;
            this.LastInitKey = initKey;
            this.Monitor.Log("CloudSync loaded. Sync enabled.", LogLevel.Info);
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            // Drain HUD message queue on the main thread (every 30 ticks ~ 0.5s)
            if (!e.IsMultipleOf(30))
                return;

            while (this.HudQueue.TryDequeue(out string? msg))
            {
                try
                {
                    Game1.addHUDMessage(new HUDMessage(msg, HUDMessage.newQuest_type));
                }
                catch
                {
                    // Game not ready yet
                }
            }
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            RegisterGmcm();

            // Apply Harmony patch for cloud icon on save load screen
            if (this.Enabled && this.CloudChecker != null)
            {
                var harmony = new Harmony(this.ModManifest.UniqueID);
                SaveFileSlotPatch.Init(this.CloudChecker, this.Monitor);
                SaveFileSlotPatch.Apply(harmony);
                SaveFileSlotPatch.LoadSprite();

                // Refresh cloud saves cache in background
                Task.Run(async () =>
                {
                    try
                    {
                        await this.CloudChecker.Refresh();
                    }
                    catch (Exception ex)
                    {
                        this.Monitor.Log(
                            $"CloudSync: cloud check failed: {ex.Message}",
                            LogLevel.Warn
                        );
                    }
                });
            }

            if (!this.Enabled) return;

            Task.Run(async () =>
            {
                try
                {
                    bool ok = await this.Client!.TestConnection();
                    if (ok)
                    {
                        this.Monitor.Log(
                            "CloudSync: connected to Nextcloud", LogLevel.Info
                        );
                        ShowHud("CloudSync: connected to Nextcloud");
                    }
                    else
                    {
                        this.Monitor.Log(
                            "CloudSync: connection failed", LogLevel.Error
                        );
                        ShowHud("CloudSync: connection failed");
                    }
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"CloudSync error: {ex}", LogLevel.Error);
                }
            });
        }

        private void RegisterGmcm()
        {
            var api = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(
                "spacechase0.GenericModConfigMenu"
            );
            if (api == null)
            {
                this.Monitor.Log(
                    "Generic Mod Config Menu not found, skipping in-game config.",
                    LogLevel.Debug
                );
                return;
            }

            api.RegisterModConfig(
                mod: this.ModManifest,
                revertToDefault: () => this.Config = new ModConfig(),
                saveToFile: () =>
                {
                    this.Helper.WriteConfig(this.Config);
                    InitSyncClient();
                }
            );

            // -- Connection --
            api.RegisterLabel(this.ModManifest, "Connection", "");

            api.RegisterSimpleOption(
                this.ModManifest, "Nextcloud URL",
                "Base URL of your Nextcloud instance (e.g. https://cloud.example.com)",
                () => this.Config.NextcloudUrl,
                val => this.Config.NextcloudUrl = val
            );

            api.RegisterSimpleOption(
                this.ModManifest, "Username",
                "Nextcloud username or app password login",
                () => this.Config.Username,
                val => this.Config.Username = val
            );

            api.RegisterSimpleOption(
                this.ModManifest, "Password / App Password",
                "Nextcloud password or app-specific password (recommended)",
                () => this.Config.Password,
                val => this.Config.Password = val
            );

            api.RegisterSimpleOption(
                this.ModManifest, "Remote Path",
                "Path on Nextcloud where sync data is stored",
                () => this.Config.RemotePath,
                val => this.Config.RemotePath = val
            );

            api.RegisterClampedOption(
                this.ModManifest, "Timeout (seconds)",
                "HTTP request timeout for WebDAV operations",
                () => this.Config.TimeoutSeconds,
                val => this.Config.TimeoutSeconds = val,
                min: 5, max: 120
            );

            // -- Sync Scope --
            api.RegisterLabel(this.ModManifest, "Sync Scope", "");

            api.RegisterSimpleOption(
                this.ModManifest, "Sync Saves",
                "Upload/download save files to Nextcloud",
                () => this.Config.SyncSaves,
                val => this.Config.SyncSaves = val
            );

            api.RegisterSimpleOption(
                this.ModManifest, "Sync Mod Configs",
                "Sync config.json files for other installed mods",
                () => this.Config.SyncModConfigs,
                val => this.Config.SyncModConfigs = val
            );

            api.RegisterSimpleOption(
                this.ModManifest, "Sync Mod Data",
                "Sync mod save data (progress, unlocks, etc.)",
                () => this.Config.SyncModData,
                val => this.Config.SyncModData = val
            );

            // -- Behavior --
            api.RegisterLabel(this.ModManifest, "Behavior", "");

            api.RegisterSimpleOption(
                this.ModManifest, "Auto Sync on Save",
                "Automatically upload after the game saves",
                () => this.Config.AutoSyncOnSave,
                val => this.Config.AutoSyncOnSave = val
            );

            api.RegisterSimpleOption(
                this.ModManifest, "Auto Sync on Load",
                "Automatically download newer saves when loading",
                () => this.Config.AutoSyncOnLoad,
                val => this.Config.AutoSyncOnLoad = val
            );

            api.RegisterSimpleOption(
                this.ModManifest, "Always Download From Cloud",
                "Always overwrite local save with cloud version on load (cloud is source of truth)",
                () => this.Config.AlwaysDownloadFromCloud,
                val => this.Config.AlwaysDownloadFromCloud = val
            );

            api.RegisterSimpleOption(
                this.ModManifest, "Manual Sync Key",
                "Press this key to trigger a full sync manually",
                () => this.Config.ManualSyncKey,
                val => this.Config.ManualSyncKey = val
            );

            api.RegisterClampedOption(
                this.ModManifest, "Max Backups",
                "Number of local backups to keep per save (for conflict resolution)",
                () => this.Config.MaxBackups,
                val => this.Config.MaxBackups = val,
                min: 0, max: 10
            );

            api.RegisterSimpleOption(
                this.ModManifest, "Show HUD Notifications",
                "Display sync status messages in-game",
                () => this.Config.ShowHudNotifications,
                val => this.Config.ShowHudNotifications = val
            );
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            if (!this.Enabled || !this.Config.AutoSyncOnLoad) return;

            Task.Run(async () =>
            {
                try
                {
                    SyncResult result;
                    if (this.Config.AlwaysDownloadFromCloud)
                    {
                        result = await this.Sync!.ForceDownloadSave();
                        if (result.Failed)
                            ShowHud("CloudSync: sync failed (check SMAPI log)");
                        else if (result.FilesDownloaded > 0)
                            ShowHud($"CloudSync: {result.FilesDownloaded} files restored from cloud");
                        else
                            ShowHud("CloudSync: no remote save found");
                    }
                    else
                    {
                        result = await this.Sync!.DownloadSaveIfNewer();
                        if (result.Failed)
                            ShowHud("CloudSync: sync failed (check SMAPI log)");
                        else if (result.HadConflict)
                            ShowHud("CloudSync: conflict resolved, backup created");
                        else if (result.FilesDownloaded > 0)
                            ShowHud("CloudSync: save downloaded (remote was newer)");
                        else
                            ShowHud("CloudSync: up to date");
                    }
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"CloudSync error: {ex}", LogLevel.Error);
                }
            });
        }

        private void OnSaved(object? sender, SavedEventArgs e)
        {
            if (!this.Enabled || !this.Config.AutoSyncOnSave) return;

            Task.Run(async () =>
            {
                try
                {
                    var result = await this.Sync!.UploadSave();
                    if (result.Failed)
                        ShowHud("CloudSync: sync failed (check SMAPI log)");
                    else
                        ShowHud("CloudSync: save uploaded");
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"CloudSync error: {ex}", LogLevel.Error);
                }
            });
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!this.Enabled || !Context.IsWorldReady) return;
            if (e.Button != this.Config.ManualSyncKey) return;

            Task.Run(async () =>
            {
                try
                {
                    ShowHud("CloudSync: syncing...");
                    var result = await this.Sync!.FullSync();
                    if (result.Failed)
                        ShowHud("CloudSync: sync failed (check SMAPI log)");
                    else if (result.HadConflict)
                        ShowHud("CloudSync: conflict resolved, backup created");
                    else if (result.FilesDownloaded > 0)
                        ShowHud($"CloudSync: {result.FilesDownloaded} files downloaded");
                    else if (result.FilesUploaded > 0)
                        ShowHud($"CloudSync: {result.FilesUploaded} files uploaded");
                    else
                        ShowHud("CloudSync: up to date");
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"CloudSync error: {ex}", LogLevel.Error);
                }
            });
        }

        private void OnCommand(string command, string[] args)
        {
            if (!this.Enabled)
            {
                this.Monitor.Log("CloudSync is not configured.", LogLevel.Warn);
                return;
            }

            string subCommand = args.FirstOrDefault()?.ToLower() ?? "status";

            switch (subCommand)
            {
                case "status":
                    string saveName = Constants.SaveFolderName ?? "(no save loaded)";
                    this.Monitor.Log($"Current save: {saveName}", LogLevel.Info);
                    this.Monitor.Log(
                        $"Nextcloud: {this.Config.NextcloudUrl}", LogLevel.Info
                    );
                    this.Monitor.Log(
                        $"Auto sync on save: {this.Config.AutoSyncOnSave}",
                        LogLevel.Info
                    );
                    this.Monitor.Log(
                        $"Auto sync on load: {this.Config.AutoSyncOnLoad}",
                        LogLevel.Info
                    );
                    break;

                case "sync":
                    Task.Run(async () =>
                    {
                        try
                        {
                            var result = await this.Sync!.FullSync();
                            this.Monitor.Log(
                                $"Sync result: {result.Message}", LogLevel.Info
                            );
                        }
                        catch (Exception ex)
                        {
                            this.Monitor.Log($"CloudSync error: {ex}", LogLevel.Error);
                        }
                    });
                    break;

                case "restore":
                    Task.Run(async () =>
                    {
                        try
                        {
                            this.Monitor.Log(
                                "CloudSync: force restoring from cloud...",
                                LogLevel.Info
                            );
                            var result = await this.Sync!.ForceDownloadSave();
                            this.Monitor.Log(
                                $"Restore result: {result.Message}", LogLevel.Info
                            );
                        }
                        catch (Exception ex)
                        {
                            this.Monitor.Log($"CloudSync error: {ex}", LogLevel.Error);
                        }
                    });
                    break;

                case "test":
                    Task.Run(async () =>
                    {
                        try
                        {
                            bool ok = await this.Client!.TestConnection();
                            this.Monitor.Log(
                                ok ? "Connection OK" : "Connection FAILED",
                                ok ? LogLevel.Info : LogLevel.Error
                            );
                        }
                        catch (Exception ex)
                        {
                            this.Monitor.Log($"CloudSync error: {ex}", LogLevel.Error);
                        }
                    });
                    break;

                case "list":
                    Task.Run(async () =>
                    {
                        try
                        {
                            string remotePath = $"{this.Config.RemotePath}/saves";
                            var resources = await this.Client!.PropFind(remotePath);
                            if (resources == null)
                            {
                                this.Monitor.Log(
                                    "No saves found on remote", LogLevel.Info
                                );
                                return;
                            }
                            foreach (var r in resources)
                            {
                                if (r.IsCollection && !r.Href.TrimEnd('/').EndsWith("saves"))
                                {
                                    string name = Uri.UnescapeDataString(
                                        r.Href.TrimEnd('/').Split('/').Last()
                                    );
                                    this.Monitor.Log(
                                        $"  {name} (modified: {r.LastModified:u})",
                                        LogLevel.Info
                                    );
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            this.Monitor.Log($"CloudSync error: {ex}", LogLevel.Error);
                        }
                    });
                    break;

                default:
                    this.Monitor.Log(
                        "Usage: cloudsync [status|sync|restore|test|list]",
                        LogLevel.Info
                    );
                    break;
            }
        }

        private void ShowHud(string message)
        {
            if (!this.Config.ShowHudNotifications) return;
            this.HudQueue.Enqueue(message);
        }
    }
}
