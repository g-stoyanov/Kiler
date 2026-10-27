namespace Kiler.Storage;

/// <summary>What a load of the local copy decides before its reads, and what it reports (M2.7, M2.7a).</summary>
public sealed partial class DataCopy
{
    /// <summary>What <see cref="Choose"/> decided for one load.</summary>
    private sealed class Plan
    {
        private readonly Dictionary<DataFolder.ListedFolder, FolderPlan> _folders = [];

        /// <summary>The folders that were listed whole, and those of a private folder that is not there, as listed empty.</summary>
        public HashSet<FolderKey> Whole { get; } = [];

        /// <summary>The folders that were also read and taken.</summary>
        public HashSet<FolderKey> Loaded { get; } = [];

        /// <summary>What the whole listings list.</summary>
        public HashSet<Spot> Listed { get; } = [];

        /// <summary>The documents that moved to the trash or back, each with the place it moved from.</summary>
        public Dictionary<Spot, Spot> Moves { get; } = [];

        /// <summary>The bytes of the budget that the plan left, for what the reads turn out to need beyond it.</summary>
        public long Slack { get; set; }

        public FolderPlan For(DataFolder.ListedFolder folder)
        {
            if (!_folders.TryGetValue(folder, out var plan))
            {
                plan = new FolderPlan();
                _folders[folder] = plan;
            }

            return plan;
        }

        /// <summary>Whether a document of the copy is no longer listed in its folder, which was listed whole.</summary>
        public bool IsLeaving(Spot spot, CopyEntry entry) => !entry.Gone && Whole.Contains(FolderOf(spot)) && !Listed.Contains(spot);
    }

    /// <summary>What <see cref="Choose"/> decided for one folder.</summary>
    private sealed class FolderPlan
    {
        /// <summary>The listed documents to read.</summary>
        public List<ListedDocument> Read { get; } = [];

        /// <summary>The listed documents that do not fit the budget.</summary>
        public List<ListedDocument> OverBudget { get; } = [];

        /// <summary>The gone documents whose last good bytes go to make room.</summary>
        public List<Spot> Evicted { get; } = [];
    }

    /// <summary>What one load gives, with the skipped entries up to a limit and a count of the rest.</summary>
    private sealed class Report(int limit)
    {
        private readonly List<SkippedEntry> _skipped = [];
        private int _more;

        public List<LoadedDocument> Changed { get; } = [];

        public List<DocumentKey> Gone { get; } = [];

        public List<OutsideChange> Outside { get; } = [];

        /// <summary>Gives a read of a document in its folder to the upper layers; those of the trash are not given.</summary>
        public void Given(Spot spot, DocumentRead read)
        {
            if (!spot.InTrash)
            {
                Changed.Add(new LoadedDocument(spot.Key, read));
            }
        }

        public void Skip(SkippedEntry entry)
        {
            if (_skipped.Count < limit)
            {
                _skipped.Add(entry);
            }
            else
            {
                _more++;
            }
        }

        /// <summary>Skips entries of a listing, each with its escaped name; a name is escaped only when it is given.</summary>
        public void Skip(string? folder, IEnumerable<FileEntry> entries, SkipReason reason)
        {
            foreach (var entry in entries)
            {
                Skip(new SkippedEntry(folder, _skipped.Count < limit ? StorageName.Escape(entry.Name) : null, reason));
            }
        }

        /// <summary>Counts skipped entries that a listing did not keep, beyond what a report gives.</summary>
        public void Untold(int count) => _more += count;

        public LoadResult Result() => new(Changed, Gone, Outside, _skipped, _more);
    }
}
