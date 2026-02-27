using System;
using System.IO;
using System.Linq;
using StardewModdingAPI;

namespace CloudSync.Sync
{
    internal enum SyncDirection
    {
        None,
        Upload,
        Download,
        Conflict
    }

    internal class ConflictResolver
    {
        private readonly int MaxBackups;
        private readonly IMonitor Monitor;

        public ConflictResolver(int maxBackups, IMonitor monitor)
        {
            this.MaxBackups = maxBackups;
            this.Monitor = monitor;
        }

        /// <summary>Determine sync direction by comparing timestamps.</summary>
        public SyncDirection Resolve(DateTime lastSync,
                                      DateTime localModified,
                                      DateTime remoteModified)
        {
            if (lastSync == DateTime.MinValue)
            {
                if (remoteModified == DateTime.MinValue)
                    return SyncDirection.Upload;
                if (localModified == DateTime.MinValue)
                    return SyncDirection.Download;

                return localModified >= remoteModified
                    ? SyncDirection.Upload
                    : SyncDirection.Download;
            }

            bool localChanged = localModified > lastSync;
            bool remoteChanged = remoteModified > lastSync;

            if (localChanged && remoteChanged)
                return SyncDirection.Conflict;
            if (localChanged)
                return SyncDirection.Upload;
            if (remoteChanged)
                return SyncDirection.Download;

            return SyncDirection.None;
        }

        /// <summary>
        /// Resolve a conflict: backup local, then newer wins.
        /// Returns the direction to proceed with after backup.
        /// </summary>
        public SyncDirection ResolveConflict(DateTime localModified,
                                              DateTime remoteModified)
        {
            return localModified >= remoteModified
                ? SyncDirection.Upload
                : SyncDirection.Download;
        }

        /// <summary>Create a backup of a local save directory.</summary>
        public void BackupLocal(string saveFolderPath)
        {
            if (!Directory.Exists(saveFolderPath))
                return;

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backupName = $"_cloudsync_backup_{timestamp}";
            string backupPath = Path.Combine(saveFolderPath, backupName);

            try
            {
                // Copy only files (not subdirectories that are backups)
                Directory.CreateDirectory(backupPath);
                foreach (string file in Directory.GetFiles(saveFolderPath))
                {
                    string dest = Path.Combine(backupPath, Path.GetFileName(file));
                    File.Copy(file, dest, overwrite: true);
                }
                this.Monitor.Log($"Backup created: {backupName}", LogLevel.Info);
                CleanupOldBackups(saveFolderPath);
            }
            catch (Exception ex)
            {
                this.Monitor.Log(
                    $"Failed to create backup: {ex.Message}", LogLevel.Error
                );
            }
        }

        private void CleanupOldBackups(string saveFolderPath)
        {
            var backups = Directory.GetDirectories(saveFolderPath)
                .Where(d => Path.GetFileName(d).StartsWith("_cloudsync_backup_"))
                .OrderByDescending(d => d)
                .ToList();

            while (backups.Count > this.MaxBackups)
            {
                string oldest = backups.Last();
                try
                {
                    Directory.Delete(oldest, recursive: true);
                    this.Monitor.Log(
                        $"Removed old backup: {Path.GetFileName(oldest)}",
                        LogLevel.Debug
                    );
                }
                catch (Exception ex)
                {
                    this.Monitor.Log(
                        $"Failed to remove backup: {ex.Message}", LogLevel.Warn
                    );
                }
                backups.RemoveAt(backups.Count - 1);
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (string file in Directory.GetFiles(source))
            {
                string dest = Path.Combine(destination, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }

            foreach (string dir in Directory.GetDirectories(source))
            {
                string dest = Path.Combine(destination, Path.GetFileName(dir));
                CopyDirectory(dir, dest);
            }
        }
    }
}
