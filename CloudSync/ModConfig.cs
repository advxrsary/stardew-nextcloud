using StardewModdingAPI;

namespace CloudSync
{
    internal class ModConfig
    {
        public string NextcloudUrl { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string RemotePath { get; set; } = "/StardewSync";
        public bool SyncSaves { get; set; } = true;
        public bool SyncModConfigs { get; set; } = true;
        public bool SyncModData { get; set; } = true;
        public bool AutoSyncOnSave { get; set; } = true;
        public bool AutoSyncOnLoad { get; set; } = true;
        public bool AlwaysDownloadFromCloud { get; set; } = false;
        public SButton ManualSyncKey { get; set; } = SButton.F6;
        public int MaxBackups { get; set; } = 3;
        public int TimeoutSeconds { get; set; } = 30;
        public bool ShowHudNotifications { get; set; } = true;
    }
}
