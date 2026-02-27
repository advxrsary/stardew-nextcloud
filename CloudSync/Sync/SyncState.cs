using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using StardewModdingAPI;

namespace CloudSync.Sync
{
    internal class SaveSyncInfo
    {
        public DateTime LastSync { get; set; }
        public DateTime LastLocalModified { get; set; }
        public DateTime LastRemoteModified { get; set; }
    }

    internal class ModConfigSyncInfo
    {
        public DateTime LastSync { get; set; }
    }

    internal class SyncStateData
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, SaveSyncInfo> Saves { get; set; } = new();
        public Dictionary<string, ModConfigSyncInfo> ModConfigs { get; set; } = new();
    }

    internal class SyncState
    {
        private readonly string FilePath;
        private readonly IMonitor Monitor;
        private SyncStateData Data;

        public SyncState(string modFolderPath, IMonitor monitor)
        {
            this.FilePath = Path.Combine(modFolderPath, "sync-state.json");
            this.Monitor = monitor;
            this.Data = Load();
        }

        private SyncStateData Load()
        {
            try
            {
                if (File.Exists(this.FilePath))
                {
                    string json = File.ReadAllText(this.FilePath);
                    return JsonSerializer.Deserialize<SyncStateData>(json)
                           ?? new SyncStateData();
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log(
                    $"Failed to load sync state: {ex.Message}", LogLevel.Warn
                );
            }

            return new SyncStateData();
        }

        public void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(this.Data, options);
                File.WriteAllText(this.FilePath, json);
            }
            catch (Exception ex)
            {
                this.Monitor.Log(
                    $"Failed to save sync state: {ex.Message}", LogLevel.Error
                );
            }
        }

        public SaveSyncInfo GetSaveInfo(string saveName)
        {
            if (!this.Data.Saves.ContainsKey(saveName))
                this.Data.Saves[saveName] = new SaveSyncInfo();
            return this.Data.Saves[saveName];
        }

        public void UpdateSaveSync(string saveName, DateTime localModified,
                                    DateTime remoteModified)
        {
            var info = GetSaveInfo(saveName);
            info.LastSync = DateTime.UtcNow;
            info.LastLocalModified = localModified;
            info.LastRemoteModified = remoteModified;
            Save();
        }

        public ModConfigSyncInfo GetModConfigInfo(string modId)
        {
            if (!this.Data.ModConfigs.ContainsKey(modId))
                this.Data.ModConfigs[modId] = new ModConfigSyncInfo();
            return this.Data.ModConfigs[modId];
        }

        public void UpdateModConfigSync(string modId)
        {
            var info = GetModConfigInfo(modId);
            info.LastSync = DateTime.UtcNow;
            Save();
        }
    }
}
