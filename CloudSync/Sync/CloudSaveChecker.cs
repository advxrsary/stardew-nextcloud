using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CloudSync.WebDav;
using StardewModdingAPI;

namespace CloudSync.Sync
{
    /// <summary>
    /// Checks which saves exist on the remote Nextcloud server.
    /// Maps cloud save folder names back to local farmers via SaveGameInfo
    /// so that the cloud icon renders correctly on the load screen.
    /// </summary>
    internal class CloudSaveChecker
    {
        private readonly WebDavClient Client;
        private readonly ModConfig Config;
        private readonly IMonitor Monitor;

        /// <summary>Cloud save folder names from PROPFIND.</summary>
        private HashSet<string> CloudSaves = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// UniqueMultiplayerID values for farmers whose saves exist in the cloud.
        /// Built by cross-referencing cloud folder names with local SaveGameInfo files.
        /// </summary>
        private HashSet<long> CloudFarmerIds = new();

        private bool Loaded;

        private static readonly Regex MultiplayerIdRegex = new(
            @"<UniqueMultiplayerID>(-?\d+)</UniqueMultiplayerID>",
            RegexOptions.Compiled
        );

        public CloudSaveChecker(WebDavClient client, ModConfig config, IMonitor monitor)
        {
            this.Client = client;
            this.Config = config;
            this.Monitor = monitor;
        }

        /// <summary>Whether the cloud saves list has been loaded.</summary>
        public bool IsLoaded => this.Loaded;

        /// <summary>Check if a save folder name exists in the cloud.</summary>
        public bool HasCloudSave(string saveFolderName)
        {
            return this.CloudSaves.Contains(saveFolderName);
        }

        /// <summary>
        /// Check if a farmer's save exists in the cloud by UniqueMultiplayerID.
        /// Uses local SaveGameInfo to map folder names to farmer IDs.
        /// </summary>
        public bool HasCloudSaveForFarmer(long uniqueMultiplayerId)
        {
            return this.CloudFarmerIds.Contains(uniqueMultiplayerId);
        }

        /// <summary>Get all cached save folder names (for diagnostics).</summary>
        public IEnumerable<string> GetAllSaves()
        {
            return this.CloudSaves;
        }

        /// <summary>Refresh the cached list of cloud saves via PROPFIND.</summary>
        public async Task Refresh()
        {
            try
            {
                string remotePath = $"{this.Config.RemotePath}/saves";
                var resources = await this.Client.PropFind(remotePath);
                var newSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (resources != null)
                {
                    foreach (var r in resources)
                    {
                        if (r.IsCollection && !r.Href.TrimEnd('/').EndsWith("saves"))
                        {
                            string name = Uri.UnescapeDataString(
                                r.Href.TrimEnd('/').Split('/').Last()
                            );
                            if (!string.IsNullOrEmpty(name))
                                newSet.Add(name);
                        }
                    }
                }

                this.CloudSaves = newSet;

                // Cross-reference cloud folders with local SaveGameInfo
                // to build a set of UniqueMultiplayerIDs for farmers in the cloud
                var farmerIds = new HashSet<long>();
                string savesPath = Constants.SavesPath;

                if (Directory.Exists(savesPath))
                {
                    foreach (string folderPath in Directory.GetDirectories(savesPath))
                    {
                        string folderName = Path.GetFileName(folderPath);
                        if (!newSet.Contains(folderName))
                            continue;

                        // This local save folder matches a cloud save -- read the farmer ID
                        string infoPath = Path.Combine(folderPath, "SaveGameInfo");
                        if (!File.Exists(infoPath))
                            continue;

                        try
                        {
                            string xml = File.ReadAllText(infoPath);
                            var match = MultiplayerIdRegex.Match(xml);
                            if (match.Success && long.TryParse(match.Groups[1].Value, out long id))
                            {
                                farmerIds.Add(id);
                                this.Monitor.Log(
                                    $"CloudSync: mapped cloud save '{folderName}' -> farmerId {id}",
                                    LogLevel.Trace
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            this.Monitor.Log(
                                $"CloudSync: failed to read SaveGameInfo for '{folderName}': {ex.Message}",
                                LogLevel.Trace
                            );
                        }
                    }
                }

                this.CloudFarmerIds = farmerIds;
                this.Loaded = true;
                this.Monitor.Log(
                    $"CloudSync: found {newSet.Count} saves in cloud, " +
                    $"matched {farmerIds.Count} to local farmers",
                    LogLevel.Debug
                );
            }
            catch (Exception ex)
            {
                this.Monitor.Log(
                    $"CloudSync: failed to check cloud saves: {ex.Message}",
                    LogLevel.Warn
                );
            }
        }

        /// <summary>Clear the cache (e.g. when config changes).</summary>
        public void Clear()
        {
            this.CloudSaves.Clear();
            this.CloudFarmerIds.Clear();
            this.Loaded = false;
        }
    }
}
