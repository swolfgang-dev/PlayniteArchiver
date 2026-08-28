using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace GameArchiver
{
    public sealed class GameArchiverPlugin : GenericPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        public override Guid Id { get; } = Guid.Parse("8f76c621-433f-4f55-b7f2-8f4fddb67ea0");
        public GameArchiverSettings Settings { get; }

        public GameArchiverPlugin(IPlayniteAPI api) : base(api)
        {
            Settings = new GameArchiverSettings(this);
            Properties = new GenericPluginProperties { HasSettings = true };
        }

        public override ISettings GetSettings(bool firstRunSettings) => Settings;
        public override UserControl GetSettingsView(bool firstRunView) => new GameArchiverSettingsView(this);

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            // Migrate archives made by version 1.0, which incorrectly marked games
            // uninstalled and therefore prevented the Play command from appearing.
            foreach (var record in Settings.Records.ToList())
            {
                var game = PlayniteApi.Database.Games[record.GameId];
                if (game != null &&
                    (!game.IsInstalled || !string.Equals(game.InstallDirectory, record.ArchivePath, StringComparison.OrdinalIgnoreCase)))
                {
                    game.InstallDirectory = record.ArchivePath;
                    game.IsInstalled = true;
                    game.OverrideInstallState = true;
                    PlayniteApi.Database.Games.Update(game);
                }
            }
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            var record = Settings.Records.FirstOrDefault(r => r.GameId == args.Game.Id);
            if (record == null)
            {
                return;
            }

            var answer = PlayniteApi.Dialogs.ShowMessage(
                $"{args.Game.Name} is archived. Restore it to the following folder before playing?{Environment.NewLine}{Environment.NewLine}{record.OriginalPath}",
                "Game Archiver",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                args.CancelStartup = true;
                return;
            }

            Exception failure = null;
            var options = new GlobalProgressOptions("Restoring " + args.Game.Name + "…", true)
            {
                IsIndeterminate = true
            };
            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                try
                {
                    RestoreOne(args.Game, progress.CancelToken, p => UpdateProgress(progress, args.Game.Name, p));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }, options);

            if (failure != null || Settings.Records.Any(r => r.GameId == args.Game.Id))
            {
                args.CancelStartup = true;
                if (!(failure is OperationCanceledException))
                {
                    if (failure != null)
                    {
                        Logger.Error(failure, "Game Archiver automatic restore failed for " + args.Game.Name);
                    }
                    PlayniteApi.Dialogs.ShowErrorMessage(failure?.Message ?? "The restore did not complete.", "Game Archiver");
                }
            }
            else
            {
                PlayniteApi.Dialogs.ShowMessage(
                    $"{args.Game.Name} was restored successfully.{Environment.NewLine}{Environment.NewLine}Playnite will now launch the game.",
                    "Game Archiver");
            }
        }

        internal void SelectArchiveRoot()
        {
            var selected = PlayniteApi.Dialogs.SelectFolder(Settings.ArchiveRoot);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                Settings.ArchiveRoot = selected;
            }
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var games = args.Games.ToList();
            var archivedIds = new HashSet<Guid>(Settings.Records.Select(r => r.GameId));
            if (games.Any(g => CanArchive(g, archivedIds)))
            {
                yield return new GameMenuItem
                {
                    MenuSection = "Game Archiver",
                    Description = "Archive selected game(s)",
                    Action = a => ArchiveGames(a.Games.Where(g =>
                        !Settings.Records.Any(r => r.GameId == g.Id) && (Settings.AllowManagedGames || g.IsCustomGame)).ToList())
                };
            }
            if (games.Any(g => archivedIds.Contains(g.Id)))
            {
                yield return new GameMenuItem
                {
                    MenuSection = "Game Archiver",
                    Description = "Restore selected game(s)",
                    Action = a => RestoreGames(a.Games.Where(g => Settings.Records.Any(r => r.GameId == g.Id)).ToList())
                };
            }
        }

        private void ArchiveGames(List<Game> games)
        {
            if (!EnsureConfigured() || !Confirm(games, "archive")) return;
            RunTransfer(games, true);
        }

        private void RestoreGames(List<Game> games)
        {
            if (!Confirm(games, "restore")) return;
            RunTransfer(games, false);
        }

        private void RunTransfer(List<Game> games, bool archive)
        {
            var failures = new List<string>();
            var completed = 0;
            var options = new GlobalProgressOptions((archive ? "Archiving" : "Restoring") + " games…", true)
            {
                IsIndeterminate = true
            };
            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                foreach (var game in games)
                {
                    if (progress.CancelToken.IsCancellationRequested) break;
                    progress.Text = (archive ? "Archiving " : "Restoring ") + game.Name;
                    try
                    {
                        if (archive) ArchiveOne(game, progress.CancelToken, p => UpdateProgress(progress, game.Name, p));
                        else RestoreOne(game, progress.CancelToken, p => UpdateProgress(progress, game.Name, p));
                        completed++;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Game Archiver transfer failed for " + game.Name);
                        failures.Add(game.Name + ": " + ex.Message);
                    }
                }
            }, options);
            if (failures.Count > 0)
                PlayniteApi.Dialogs.ShowErrorMessage(string.Join(Environment.NewLine + Environment.NewLine, failures), "Game Archiver");
            if (completed > 0)
            {
                var operation = archive ? "archived" : "restored";
                var message = $"Successfully {operation} {completed} game(s).";
                if (failures.Count > 0)
                {
                    message += $"{Environment.NewLine}{Environment.NewLine}{failures.Count} game(s) failed.";
                }
                PlayniteApi.Dialogs.ShowMessage(message, "Game Archiver");
            }
        }

        private void ArchiveOne(Game game, CancellationToken token, Action<DirectoryTransferProgress> progress = null)
        {
            if (!Settings.AllowManagedGames && !game.IsCustomGame)
                throw new InvalidOperationException("Launcher-managed games are disabled in the extension settings.");
            if (Settings.Records.Any(r => r.GameId == game.Id)) throw new InvalidOperationException("Game is already archived.");
            if (string.IsNullOrWhiteSpace(game.InstallDirectory)) throw new InvalidOperationException("Install Directory is empty.");
            var source = Path.GetFullPath(PlayniteApi.ExpandGameVariables(game, game.InstallDirectory));
            var destination = BuildArchivePath(source);
            var originalInstalled = game.IsInstalled;
            var originalOverride = game.OverrideInstallState;
            var archivedTag = FindArchivedTag();
            var originallyTagged = archivedTag != null && game.TagIds != null && game.TagIds.Contains(archivedTag.Id);
            ArchiveRecord record = null;
            try
            {
                DirectoryTransfer.MoveSafely(source, destination, token, progress);
                record = new ArchiveRecord { GameId = game.Id, OriginalPath = source, ArchivePath = destination, ArchivedAt = DateTime.UtcNow };
                Settings.Records.Add(record);
                AddArchivedTag(game);
                // Keep the game installed so Play remains available. OnGameStarting
                // restores archived files before Playnite executes the game action.
                game.InstallDirectory = destination;
                game.IsInstalled = true;
                game.OverrideInstallState = true;
                PlayniteApi.Database.Games.Update(game);
                SavePluginSettings(Settings);
            }
            catch (Exception error)
            {
                var cleanupErrors = new List<Exception>();
                TryRollbackMove(destination, source, cleanupErrors);
                if (record != null) Settings.Records.Remove(record);
                game.InstallDirectory = source;
                game.IsInstalled = originalInstalled;
                game.OverrideInstallState = originalOverride;
                if (!originallyTagged) RemoveArchivedTag(game);
                TryRestoreMetadata(game, cleanupErrors);
                ThrowWithCleanupErrors(error, cleanupErrors);
            }
        }

        private void RestoreOne(Game game, CancellationToken token, Action<DirectoryTransferProgress> progress = null)
        {
            var record = Settings.Records.FirstOrDefault(r => r.GameId == game.Id);
            if (record == null) throw new InvalidOperationException("No archive record exists for this game.");
            var originalInstalled = game.IsInstalled;
            var originalOverride = game.OverrideInstallState;
            try
            {
                DirectoryTransfer.MoveSafely(record.ArchivePath, record.OriginalPath, token, progress);
                Settings.Records.Remove(record);
                RemoveArchivedTag(game);
                game.InstallDirectory = record.OriginalPath;
                game.IsInstalled = true;
                game.OverrideInstallState = true;
                PlayniteApi.Database.Games.Update(game);
                SavePluginSettings(Settings);
            }
            catch (Exception error)
            {
                var cleanupErrors = new List<Exception>();
                TryRollbackMove(record.OriginalPath, record.ArchivePath, cleanupErrors);
                if (!Settings.Records.Contains(record)) Settings.Records.Add(record);
                AddArchivedTag(game);
                game.InstallDirectory = record.ArchivePath;
                game.IsInstalled = originalInstalled;
                game.OverrideInstallState = originalOverride;
                TryRestoreMetadata(game, cleanupErrors);
                ThrowWithCleanupErrors(error, cleanupErrors);
            }
        }

        private void TryRollbackMove(string currentPath, string previousPath, List<Exception> errors)
        {
            try
            {
                if (Directory.Exists(currentPath) && !Directory.Exists(previousPath))
                {
                    DirectoryTransfer.MoveSafely(currentPath, previousPath, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        private void TryRestoreMetadata(Game game, List<Exception> errors)
        {
            try
            {
                PlayniteApi.Database.Games.Update(game);
                SavePluginSettings(Settings);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        private static void ThrowWithCleanupErrors(Exception original, List<Exception> cleanupErrors)
        {
            if (cleanupErrors.Count == 0)
            {
                throw original;
            }
            var allErrors = new List<Exception> { original };
            allErrors.AddRange(cleanupErrors);
            throw new AggregateException("The operation failed and rollback could not be completed. No data was intentionally discarded.", allErrors);
        }

        private static void UpdateProgress(GlobalProgressActionArgs dialog, string gameName, DirectoryTransferProgress progress)
        {
            var maximum = Math.Max(1L, progress.TotalBytes);
            dialog.IsIndeterminate = false;
            dialog.ProgressMaxValue = maximum;
            dialog.CurrentProgressValue = Math.Min(progress.BytesCopied, maximum);
            dialog.Text = $"{gameName}{Environment.NewLine}{FormatBytes(progress.BytesCopied)} / {FormatBytes(progress.TotalBytes)}{Environment.NewLine}{progress.CurrentFile}";
        }

        private static string FormatBytes(long bytes)
        {
            var units = new[] { "B", "KB", "MB", "GB", "TB" };
            double value = Math.Max(0, bytes);
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
        }

        private string BuildArchivePath(string source)
        {
            var root = Path.GetFullPath(Settings.ArchiveRoot).TrimEnd(Path.DirectorySeparatorChar);
            var match = Settings.GetSourceRoots()
                .Where(r => source.Equals(r, StringComparison.OrdinalIgnoreCase) || source.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Length).FirstOrDefault();
            string relative;
            if (match != null) relative = source.Substring(match.Length).TrimStart(Path.DirectorySeparatorChar);
            else
            {
                var drive = (Path.GetPathRoot(source) ?? "drive").TrimEnd(Path.DirectorySeparatorChar).Replace(":", string.Empty);
                relative = Path.Combine(drive, source.Substring((Path.GetPathRoot(source) ?? string.Empty).Length));
            }
            if (string.IsNullOrWhiteSpace(relative)) relative = new DirectoryInfo(source).Name;
            return Path.Combine(root, relative);
        }

        private void AddArchivedTag(Game game)
        {
            var tag = FindArchivedTag();
            if (tag == null)
            {
                tag = new Tag(Settings.ArchivedTagName);
                PlayniteApi.Database.Tags.Add(tag);
            }
            if (game.TagIds == null) game.TagIds = new List<Guid>();
            if (!game.TagIds.Contains(tag.Id)) game.TagIds.Add(tag.Id);
        }

        private void RemoveArchivedTag(Game game)
        {
            var tag = FindArchivedTag();
            if (tag != null && game.TagIds != null) game.TagIds.Remove(tag.Id);
        }

        private Tag FindArchivedTag() => PlayniteApi.Database.Tags.FirstOrDefault(
            t => string.Equals(t.Name, Settings.ArchivedTagName, StringComparison.OrdinalIgnoreCase));

        private bool CanArchive(Game game, HashSet<Guid> archivedIds) =>
            !archivedIds.Contains(game.Id) && (Settings.AllowManagedGames || game.IsCustomGame);

        private bool EnsureConfigured()
        {
            if (!string.IsNullOrWhiteSpace(Settings.ArchiveRoot)) return true;
            PlayniteApi.Dialogs.ShowMessage("Set an archive root in Add-ons → Extension settings → Game Archiver first.", "Game Archiver");
            return false;
        }

        private bool Confirm(List<Game> games, string operation) => games.Count > 0 &&
            PlayniteApi.Dialogs.ShowMessage($"{char.ToUpper(operation[0]) + operation.Substring(1)} {games.Count} selected game(s)?", "Game Archiver", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
    }
}
