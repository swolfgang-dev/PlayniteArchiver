# Game Archiver for Playnite

A Playnite desktop extension for moving games between fast storage and cold storage.

## Features

- Archive and restore one or many selected games from the right-click menu.
- Remembers each game's exact original location.
- Preserves directory structure beneath configurable source roots.
- Adds/removes a configurable `Archived` tag.
- Keeps archived entries playable and offers to restore them when Play is pressed.
- Switches Playnite's install directory to the current physical location, so Show in Explorer and installation detection continue to work.
- Shows completion confirmations after archive, restore, and restore-before-play operations.
- Shows Archive and Restore context commands only when the selection contains eligible games.
- Rolls back file moves and Playnite metadata when an archive or restore operation fails, and removes partial transfer folders.
- Optionally supports launcher-managed games. This is enabled by default but does not update external launcher databases.
- Reports byte-level progress and the current file for each cross-drive archive or restore transfer.
- Uses a temporary staging directory for cross-drive copies and only removes the source after the copy completes.
- Refuses destination collisions and non-manual games.

## Build and install

1. Build with Visual Studio 2022 or `dotnet build -c Release` on Windows.
2. Copy the contents of `bin/Release` into a folder under `%AppData%\Playnite\Extensions`.
3. Restart Playnite.
4. Set the archive root under **Add-ons → Extension settings → Game Archiver**.

For development, add the build output directory in Playnite under **Settings → For developers → External extensions**.

## Usage

Select manually added games and use **Game Archiver → Archive selected game(s)** or **Restore selected game(s)**. Archived games remain enabled in the library. Pressing **Play** asks whether to restore the game to its original folder, restores it, and then allows the normal game action to continue.

Launcher-managed games can be enabled or disabled in settings. The extension only moves their files and updates Playnite; it does not update Steam, Epic, EA, GOG, or other external launcher databases. Restore these games before launching them through Playnite.
