using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace GameArchiver
{
    public sealed class ArchiveRecord
    {
        public Guid GameId { get; set; }
        public string OriginalPath { get; set; }
        public string ArchivePath { get; set; }
        public DateTime ArchivedAt { get; set; }
    }

    public sealed class GameArchiverSettings : ObservableObject, ISettings
    {
        private readonly GameArchiverPlugin plugin;
        private GameArchiverSettings editingClone;
        private string archiveRoot = string.Empty;
        private string sourceRoots = string.Empty;
        private string archivedTagName = "Archived";
        private bool allowManagedGames = true;

        public string ArchiveRoot { get => archiveRoot; set => SetValue(ref archiveRoot, value); }
        public string SourceRoots { get => sourceRoots; set => SetValue(ref sourceRoots, value); }
        public string ArchivedTagName { get => archivedTagName; set => SetValue(ref archivedTagName, value); }
        public bool AllowManagedGames { get => allowManagedGames; set => SetValue(ref allowManagedGames, value); }
        public List<ArchiveRecord> Records { get; set; } = new List<ArchiveRecord>();

        public GameArchiverSettings() { }

        public GameArchiverSettings(GameArchiverPlugin plugin)
        {
            this.plugin = plugin;
            var saved = plugin.LoadPluginSettings<GameArchiverSettings>();
            if (saved != null)
            {
                ArchiveRoot = saved.ArchiveRoot ?? string.Empty;
                SourceRoots = saved.SourceRoots ?? string.Empty;
                ArchivedTagName = string.IsNullOrWhiteSpace(saved.ArchivedTagName) ? "Archived" : saved.ArchivedTagName;
                AllowManagedGames = saved.AllowManagedGames;
                Records = saved.Records ?? new List<ArchiveRecord>();
            }
        }

        public IEnumerable<string> GetSourceRoots() =>
            (SourceRoots ?? string.Empty).Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => Path.GetFullPath(p.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase);

        public void BeginEdit()
        {
            editingClone = new GameArchiverSettings
            {
                ArchiveRoot = ArchiveRoot,
                SourceRoots = SourceRoots,
                ArchivedTagName = ArchivedTagName,
                AllowManagedGames = AllowManagedGames
            };
        }

        public void CancelEdit()
        {
            if (editingClone == null) return;
            ArchiveRoot = editingClone.ArchiveRoot;
            SourceRoots = editingClone.SourceRoots;
            ArchivedTagName = editingClone.ArchivedTagName;
        }

        public void EndEdit() => plugin.SavePluginSettings(this);

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (string.IsNullOrWhiteSpace(ArchiveRoot)) errors.Add("Choose an archive root folder.");
            else
            {
                try { Path.GetFullPath(ArchiveRoot); }
                catch { errors.Add("Archive root is not a valid path."); }
            }
            if (string.IsNullOrWhiteSpace(ArchivedTagName)) errors.Add("Archived tag name cannot be empty.");
            return errors.Count == 0;
        }
    }
}
