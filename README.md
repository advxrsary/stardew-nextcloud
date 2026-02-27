# CloudSync

A SMAPI mod for Stardew Valley that automatically syncs your saves, mod configs, and mod data to a self-hosted cloud via WebDAV.

Works with Nextcloud, ownCloud, and any WebDAV-compatible storage.

---

## Features

- **Auto-sync on save/load** -- uploads after saving, downloads newer saves on load
- **Cloud restore** -- force download from cloud via console command or config toggle
- **Mod config sync** -- keeps mod settings in sync across machines (only for currently installed mods)
- **Cloud icon on load screen** -- cloud indicator on save slots that exist in the cloud, with a tooltip on hover
- **Manual sync hotkey** -- press F6 (configurable) to trigger sync anytime
- **Conflict resolution** -- compares timestamps, creates local backups before overwriting
- **GMCM support** -- configure everything in-game via Generic Mod Config Menu

## Requirements

| Requirement | Version |
|---|---|
| Stardew Valley | 1.6+ |
| SMAPI | 4.0+ |
| WebDAV server | Nextcloud, ownCloud, or any WebDAV-compatible provider |
| Generic Mod Config Menu | 1.16+ (optional, for in-game settings) |

## Installation

1. Install [SMAPI](https://smapi.io/) if you haven't already
2. Download the latest release and extract the `CloudSync` folder into your `Mods` directory
3. Launch the game once to generate `config.json`
4. Edit `Mods/CloudSync/config.json` (or use GMCM in-game) with your WebDAV credentials:

```json
{
  "NextcloudUrl": "https://cloud.example.com",
  "Username": "your-username",
  "Password": "your-password",
  "RemotePath": "/StardewSync"
}
```

5. Relaunch the game -- saves will sync automatically

## Configuration

| Option | Default | Description |
|---|---|---|
| `NextcloudUrl` | `""` | WebDAV server base URL |
| `Username` | `""` | WebDAV username |
| `Password` | `""` | WebDAV password |
| `RemotePath` | `/StardewSync` | Remote folder path for synced data |
| `SyncSaves` | `true` | Sync save files |
| `SyncModConfigs` | `true` | Sync mod config.json files |
| `AutoSyncOnSave` | `true` | Auto-upload after saving |
| `AutoSyncOnLoad` | `true` | Auto-download on load if cloud is newer |
| `AlwaysDownloadFromCloud` | `false` | Always overwrite local with cloud version on load |
| `ManualSyncKey` | `F6` | Hotkey for manual sync |
| `MaxBackups` | `3` | Local backups to keep before overwriting |
| `TimeoutSeconds` | `30` | HTTP request timeout |
| `ShowHudNotifications` | `true` | Show HUD messages for sync status |

## Console Commands

Open the SMAPI console (`~` key) and type:

| Command | Description |
|---|---|
| `cloudsync status` | Show sync status and connection info |
| `cloudsync sync` | Full sync: download newer, then upload local changes |
| `cloudsync restore` | Force download from cloud (ignores timestamps) |
| `cloudsync test` | Test WebDAV connection |
| `cloudsync list` | List saves stored in the cloud |

## How It Works

CloudSync uses the WebDAV protocol (PROPFIND, GET, PUT, MKCOL) to communicate with your cloud server. The remote folder structure mirrors your local data:

```
/StardewSync/
  saves/
    FarmName_123456789/
      FarmName_123456789
      SaveGameInfo
      ...
  mod-configs/
    ModName/
      config.json
```

**Conflict resolution**: when both local and cloud versions have changed since the last sync, the mod compares timestamps. The newer version wins. A local backup is always created before any overwrite.

**Mod config sync**: only syncs configs for mods that are currently installed. The mod's own `config.json` (which contains credentials) is excluded from sync.

## Multi-Machine Setup

This mod is designed for players who play on multiple machines:

1. Install CloudSync on all machines with the same WebDAV credentials
2. Play on Machine A -- saves auto-upload on save
3. Switch to Machine B -- saves auto-download on load (if cloud version is newer)
4. Mod configs stay in sync so your settings follow you

For a "cloud is always right" setup, enable `AlwaysDownloadFromCloud` in config.

## Compatibility

- The cloud icon on the save load screen is positioned to the left of the delete button
- Uses Harmony patches on `SaveFileSlot.Draw` (postfix only, non-destructive)

## Building from Source

Requires .NET 6.0 SDK and Stardew Valley installed.

```bash
git clone https://github.com/advxrsary/stardew-nextcloud.git
cd stardew-nextcloud
dotnet build CloudSync/CloudSync.csproj -c Release
```

The built mod will be deployed to your game's `Mods/` folder automatically. A release zip is generated at `CloudSync/bin/Release/net6.0/CloudSync 0.1.0.zip`.

## License

MIT
