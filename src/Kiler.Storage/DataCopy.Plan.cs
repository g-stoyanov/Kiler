namespace Kiler.Storage;

/// <summary>What a load of the local copy decides before its reads, and what it reports (M2.7, M2.7a).</summary>
public sealed partial class DataCopy
{
    /// <summary>What <see cref="Choose"/> decided for one pass of a load, in the room of its budget.</summary>
    private sealed class Plan(Room room)
    {
        private readonly Dictionary<DataFolder.ListedFolder, FolderPlan> _folders = [];

        /// <summary>The room of the budget, which the plan filled, and whose bytes that are left go to what the reads turn out to need beyond it.</summary>
        public Room Room => room;

        /// <summary>The folders that were listed whole, and those of a private folder that is not there, as listed empty.</summary>
        public HashSet<FolderKey> Whole { get; } = [];

        /// <summary>The folders that were also read and taken.</summary>
        public HashSet<FolderKey> Loaded { get; } = [];

        /// <summary>What the whole listings list.</summary>
        public HashSet<Spot> Listed { get; } = [];

        /// <summary>The documents that moved to the trash or back, each with the place it moved from.</summary>
        public Dictionary<Spot, Spot> Moves { get; } = [];

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

    /// <summary>
    /// A budget of the copy (M2.7c): the entries that it counts, its bytes and documents, and the budget that it is within,
    /// which counts them too. The user's own is the data folder with their private folder, in one budget (M2.7b); the private
    /// folder of another user has one of its own within that of all of them (decided in M2.7c, by the user).
    /// </summary>
    private sealed record Cap(Func<Spot, bool> Counts, long Bytes, int Documents, Cap? Within);

    /// <summary>
    /// The room in a budget during one pass of a load: the bytes and the documents that it holds, and the brief leftovers that
    /// may make room in it, those that went first first (<see cref="BriefStay"/>). Room in a budget that is within another is
    /// room in both, and only its own leftovers make it, so the private folder of one user never makes room for another.
    /// </summary>
    private sealed class Room(Cap cap)
    {
        private readonly Queue<KeyValuePair<Spot, CopyEntry>> _leftovers = new();
        private long _leftoverBytes;

        public Cap Cap => cap;

        /// <summary>The room of the budget that this one is within.</summary>
        public Room? Within { get; } = cap.Within is { } outer ? new Room(outer) : null;

        public long Bytes { get; private set; }

        public int Count { get; private set; }

        /// <summary>The bytes that are left, in this budget and in the one it is within.</summary>
        public long Spare => Math.Max(0, Math.Min(cap.Bytes - Bytes, Within?.Spare ?? long.MaxValue));

        /// <summary>Whether it holds more than its budget, or the one it is within does.</summary>
        public bool IsOver => Bytes > cap.Bytes || Count > cap.Documents || Within?.IsOver == true;

        /// <summary>Counts bytes and documents in it, and in the budget it is within; fewer, when they are negative.</summary>
        public void Add(long bytes, int documents)
        {
            Bytes += bytes;
            Count += documents;
            Within?.Add(bytes, documents);
        }

        /// <summary>The brief leftovers that may make room, in the order they go.</summary>
        public void MayEvict(IEnumerable<KeyValuePair<Spot, CopyEntry>> leftovers)
        {
            foreach (var leftover in leftovers)
            {
                _leftovers.Enqueue(leftover);
                _leftoverBytes += Cost(leftover.Value);
            }
        }

        /// <summary>
        /// Takes more bytes and documents, when they fit once as many brief leftovers as it takes have gone into
        /// <paramref name="evicted"/>, and none when even all of them would not make them fit.
        /// </summary>
        public bool Take(long more, int documents, List<Spot> evicted)
        {
            if (!Fits(more, documents, _leftoverBytes, _leftovers.Count))
            {
                return false;
            }

            while (!Fits(more, documents, 0, 0))
            {
                var gone = _leftovers.Dequeue();
                var cost = Cost(gone.Value);
                _leftoverBytes -= cost;
                Add(-cost, -1);
                evicted.Add(gone.Key);
            }

            Add(more, documents);
            return true;
        }

        private bool Fits(long more, int documents, long freed, int freedCount) =>
            Bytes + more - freed <= cap.Bytes
            && Count + documents - freedCount <= cap.Documents
            && (Within is null || Within.Fits(more, documents, freed, freedCount));
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

        /// <summary>Whether the documents in the trash that the copy gives changed (M2.8).</summary>
        public bool TrashChanged { get; private set; }

        /// <summary>
        /// Gives a read of a document in its folder to the upper layers; one of the trash is not given, and says that the trash
        /// changed.
        /// </summary>
        public void Given(Spot spot, DocumentRead read)
        {
            if (spot.InTrash)
            {
                TrashChanged = true;
            }
            else
            {
                Changed.Add(new LoadedDocument(spot.Key, read));
            }
        }

        /// <summary>A document of the user's own has gone from the trash.</summary>
        public void LeftTheTrash() => TrashChanged = true;

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

        public LoadResult Result() => new(Changed, Gone, Outside, _skipped, _more, TrashChanged);
    }
}
