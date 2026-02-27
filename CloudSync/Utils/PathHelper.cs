using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StardewModdingAPI;

namespace CloudSync.Utils
{
    internal class PathHelper
    {
        private readonly IModHelper Helper;

        public PathHelper(IModHelper helper)
        {
            this.Helper = helper;
        }

        /// <summary>Get the full path to the current save folder.</summary>
        public string GetCurrentSavePath()
        {
            return Path.Combine(
                Constants.SavesPath,
                Constants.SaveFolderName ?? ""
            );
        }

        /// <summary>Get all files in a save folder (excluding backup dirs).</summary>
        public List<string> GetSaveFiles(string saveFolderPath)
        {
            if (!Directory.Exists(saveFolderPath))
                return new List<string>();

            return Directory.GetFiles(saveFolderPath)
                .Where(f =>
                {
                    string name = Path.GetFileName(f);
                    return !name.StartsWith(".") && !name.StartsWith("_cloudsync_");
                })
                .ToList();
        }

        /// <summary>Get last-modified time for a save folder (newest file).</summary>
        public DateTime GetSaveLastModified(string saveFolderPath)
        {
            var files = GetSaveFiles(saveFolderPath);
            if (files.Count == 0)
                return DateTime.MinValue;

            return files.Max(f => File.GetLastWriteTimeUtc(f));
        }

        /// <summary>
        /// Get all mod config.json files from the Mods directory.
        /// Returns dict of modFolderName -> configFilePath.
        /// </summary>
        public Dictionary<string, string> GetModConfigs()
        {
            var result = new Dictionary<string, string>();
            // Helper.DirectoryPath is this mod's folder inside Mods/,
            // so go up one level to get the Mods directory itself.
            string modsPath = Path.GetDirectoryName(this.Helper.DirectoryPath) ?? "";

            if (!Directory.Exists(modsPath))
                return result;

            foreach (string modDir in Directory.GetDirectories(modsPath))
            {
                string modName = Path.GetFileName(modDir);

                // Skip our own config (contains credentials)
                if (modName == "CloudSync")
                    continue;

                string configPath = Path.Combine(modDir, "config.json");
                if (File.Exists(configPath))
                    result[modName] = configPath;
            }

            return result;
        }
    }
}
