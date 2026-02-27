using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudSync.Utils;
using CloudSync.WebDav;
using StardewModdingAPI;

namespace CloudSync.Sync
{
    internal class SyncResult
    {
        public int FilesUploaded { get; set; }
        public int FilesDownloaded { get; set; }
        public bool HadConflict { get; set; }
        public bool Failed { get; set; }
        public string Message { get; set; } = "";
    }

    internal class SyncManager
    {
        private readonly WebDavClient Client;
        private readonly SyncState State;
        private readonly ConflictResolver Conflicts;
        private readonly PathHelper Paths;
        private readonly ModConfig Config;
        private readonly IMonitor Monitor;
        private int _syncing;

        public SyncManager(WebDavClient client, SyncState state,
                           ConflictResolver conflicts, PathHelper paths,
                           ModConfig config, IMonitor monitor)
        {
            this.Client = client;
            this.State = state;
            this.Conflicts = conflicts;
            this.Paths = paths;
            this.Config = config;
            this.Monitor = monitor;
        }

        /// <summary>Upload current save to Nextcloud.</summary>
        public async Task<SyncResult> UploadSave()
        {
            if (Interlocked.CompareExchange(ref _syncing, 1, 0) != 0)
                return new SyncResult { Message = "Sync already in progress" };

            try
            {
                return await UploadSaveInternal();
            }
            finally
            {
                Interlocked.Exchange(ref _syncing, 0);
            }
        }

        private async Task<SyncResult> UploadSaveInternal()
        {
            var result = new SyncResult();

            try
            {
                string? saveName = Constants.SaveFolderName;
                if (string.IsNullOrEmpty(saveName))
                {
                    result.Message = "No save loaded";
                    return result;
                }

                string savePath = this.Paths.GetCurrentSavePath();
                string remoteSavePath = $"{this.Config.RemotePath}/saves/{saveName}";

                // Ensure remote directory exists
                await this.Client.CreateDirectory(remoteSavePath);

                // Upload save files
                if (this.Config.SyncSaves)
                {
                    var files = this.Paths.GetSaveFiles(savePath);
                    foreach (string file in files)
                    {
                        string fileName = Path.GetFileName(file);
                        string remotePath = $"{remoteSavePath}/{fileName}";
                        bool ok = await this.Client.UploadFile(remotePath, file);
                        if (ok)
                            result.FilesUploaded++;
                    }
                }

                // Upload mod configs
                if (this.Config.SyncModConfigs)
                {
                    var configs = this.Paths.GetModConfigs();
                    foreach (var (modName, configPath) in configs)
                    {
                        string remotePath =
                            $"{this.Config.RemotePath}/mod-configs/{modName}/config.json";
                        bool ok = await this.Client.UploadFile(remotePath, configPath);
                        if (ok)
                        {
                            result.FilesUploaded++;
                            this.State.UpdateModConfigSync(modName);
                        }
                    }
                }

                // Update sync state
                DateTime localMod = this.Paths.GetSaveLastModified(savePath);
                this.State.UpdateSaveSync(saveName, localMod, DateTime.UtcNow);

                result.Message = $"{result.FilesUploaded} files uploaded";
                this.Monitor.Log(
                    $"CloudSync: upload complete ({result.Message})", LogLevel.Info
                );
            }
            catch (Exception ex)
            {
                result.Failed = true;
                result.Message = ex.Message;
                this.Monitor.Log(
                    $"CloudSync: upload failed: {ex.Message}", LogLevel.Error
                );
            }
            return result;
        }

        /// <summary>Download save from Nextcloud if remote is newer.</summary>
        public async Task<SyncResult> DownloadSaveIfNewer()
        {
            if (Interlocked.CompareExchange(ref _syncing, 1, 0) != 0)
                return new SyncResult { Message = "Sync already in progress" };

            try
            {
                return await DownloadSaveIfNewerInternal();
            }
            finally
            {
                Interlocked.Exchange(ref _syncing, 0);
            }
        }

        private async Task<SyncResult> DownloadSaveIfNewerInternal()
        {
            var result = new SyncResult();

            try
            {
                string? saveName = Constants.SaveFolderName;
                if (string.IsNullOrEmpty(saveName))
                {
                    result.Message = "No save loaded";
                    return result;
                }

                string savePath = this.Paths.GetCurrentSavePath();
                string remoteSavePath = $"{this.Config.RemotePath}/saves/{saveName}";

                // Get remote metadata
                var remoteResources = await this.Client.PropFind(remoteSavePath);
                if (remoteResources == null || remoteResources.Count == 0)
                {
                    result.Message = "No remote save found";
                    this.Monitor.Log(
                        $"CloudSync: no remote save for {saveName}", LogLevel.Debug
                    );
                    return result;
                }

                // Get timestamps
                DateTime remoteMod = DateTime.MinValue;
                foreach (var r in remoteResources)
                {
                    if (!r.IsCollection && r.LastModified > remoteMod)
                        remoteMod = r.LastModified;
                }

                DateTime localMod = this.Paths.GetSaveLastModified(savePath);
                var syncInfo = this.State.GetSaveInfo(saveName);

                // Determine direction
                var direction = this.Conflicts.Resolve(
                    syncInfo.LastSync, localMod, remoteMod
                );

                switch (direction)
                {
                    case SyncDirection.None:
                        result.Message = "Already up to date";
                        this.Monitor.Log("CloudSync: up to date", LogLevel.Debug);
                        break;

                    case SyncDirection.Upload:
                        result.Message = "Local is newer, will upload on save";
                        this.Monitor.Log(
                            "CloudSync: local is newer", LogLevel.Debug
                        );
                        break;

                    case SyncDirection.Download:
                        result = await DownloadSave(saveName, savePath,
                                                     remoteSavePath);
                        this.State.UpdateSaveSync(saveName, localMod, remoteMod);
                        break;

                    case SyncDirection.Conflict:
                        this.Monitor.Log(
                            "CloudSync: conflict detected, backing up local",
                            LogLevel.Warn
                        );
                        this.Conflicts.BackupLocal(savePath);
                        result.HadConflict = true;

                        var resolved = this.Conflicts.ResolveConflict(
                            localMod, remoteMod
                        );
                        if (resolved == SyncDirection.Download)
                        {
                            result = await DownloadSave(saveName, savePath,
                                                         remoteSavePath);
                            result.HadConflict = true;
                        }
                        this.State.UpdateSaveSync(saveName, localMod, remoteMod);
                        break;
                }

                // Sync mod configs (download if remote newer)
                if (this.Config.SyncModConfigs)
                    await SyncModConfigsDown();
            }
            catch (Exception ex)
            {
                result.Failed = true;
                result.Message = ex.Message;
                this.Monitor.Log(
                    $"CloudSync: download check failed: {ex.Message}",
                    LogLevel.Error
                );
            }
            return result;
        }

        /// <summary>Force download save from Nextcloud, ignoring timestamps.</summary>
        public async Task<SyncResult> ForceDownloadSave()
        {
            if (Interlocked.CompareExchange(ref _syncing, 1, 0) != 0)
                return new SyncResult { Message = "Sync already in progress" };

            try
            {
                return await ForceDownloadSaveInternal();
            }
            finally
            {
                Interlocked.Exchange(ref _syncing, 0);
            }
        }

        private async Task<SyncResult> ForceDownloadSaveInternal()
        {
            var result = new SyncResult();

            try
            {
                string? saveName = Constants.SaveFolderName;
                if (string.IsNullOrEmpty(saveName))
                {
                    result.Message = "No save loaded";
                    return result;
                }

                string savePath = this.Paths.GetCurrentSavePath();
                string remoteSavePath = $"{this.Config.RemotePath}/saves/{saveName}";

                // Check remote exists
                var remoteResources = await this.Client.PropFind(remoteSavePath);
                if (remoteResources == null || remoteResources.Count == 0)
                {
                    result.Message = "No remote save found";
                    return result;
                }

                // Backup local before overwrite
                this.Conflicts.BackupLocal(savePath);

                // Download all remote files
                result = await DownloadSave(saveName, savePath, remoteSavePath);

                // Update sync state
                DateTime localMod = this.Paths.GetSaveLastModified(savePath);
                DateTime remoteMod = DateTime.MinValue;
                foreach (var r in remoteResources)
                {
                    if (!r.IsCollection && r.LastModified > remoteMod)
                        remoteMod = r.LastModified;
                }
                this.State.UpdateSaveSync(saveName, localMod, remoteMod);

                this.Monitor.Log(
                    $"CloudSync: force download complete ({result.Message})",
                    LogLevel.Info
                );
            }
            catch (Exception ex)
            {
                result.Failed = true;
                result.Message = ex.Message;
                this.Monitor.Log(
                    $"CloudSync: force download failed: {ex.Message}",
                    LogLevel.Error
                );
            }
            return result;
        }

        /// <summary>Full manual sync: download newer, upload local changes.</summary>
        public async Task<SyncResult> FullSync()
        {
            if (Interlocked.CompareExchange(ref _syncing, 1, 0) != 0)
                return new SyncResult { Message = "Sync already in progress" };

            try
            {
                var downResult = await DownloadSaveIfNewerInternal();
                if (downResult.Failed)
                    return downResult;

                if (downResult.FilesDownloaded > 0)
                    return downResult;

                return await UploadSaveInternal();
            }
            finally
            {
                Interlocked.Exchange(ref _syncing, 0);
            }
        }

        private async Task<SyncResult> DownloadSave(string saveName,
                                                      string savePath,
                                                      string remoteSavePath)
        {
            var result = new SyncResult();
            var remoteResources = await this.Client.PropFind(remoteSavePath);
            if (remoteResources == null)
            {
                result.Failed = true;
                result.Message = "Failed to list remote files";
                return result;
            }

            foreach (var resource in remoteResources)
            {
                if (resource.IsCollection)
                    continue;

                // Extract filename from href
                string fileName = Uri.UnescapeDataString(
                    resource.Href.TrimEnd('/').Split('/').Last()
                );
                if (string.IsNullOrEmpty(fileName))
                    continue;

                string remotePath = $"{remoteSavePath}/{fileName}";
                string localPath = Path.Combine(savePath, fileName);

                bool ok = await this.Client.DownloadFile(remotePath, localPath);
                if (ok)
                    result.FilesDownloaded++;
            }

            result.Message = $"{result.FilesDownloaded} files downloaded";
            this.Monitor.Log(
                $"CloudSync: download complete ({result.Message})", LogLevel.Info
            );
            return result;
        }

        private async Task SyncModConfigsDown()
        {
            var configs = this.Paths.GetModConfigs();
            foreach (var (modName, localPath) in configs)
            {
                string remotePath =
                    $"{this.Config.RemotePath}/mod-configs/{modName}/config.json";

                DateTime? remoteMod = await this.Client.GetLastModified(remotePath);
                if (remoteMod == null)
                    continue;

                DateTime localMod = File.GetLastWriteTimeUtc(localPath);
                var syncInfo = this.State.GetModConfigInfo(modName);

                var direction = this.Conflicts.Resolve(
                    syncInfo.LastSync, localMod, remoteMod.Value
                );

                if (direction == SyncDirection.Download ||
                    direction == SyncDirection.Conflict)
                {
                    await this.Client.DownloadFile(remotePath, localPath);
                    this.State.UpdateModConfigSync(modName);
                }
            }
        }
    }
}
