using System.Globalization;

namespace Kiler.Storage;

/// <summary>
/// The audit of the secret values (M6.17, design 12): <c>audit\&lt;day&gt;\&lt;account&gt;.jsonl</c> in the data folder, one line
/// per view, each ending in <c>\n</c>, so two users never write one file; two Kilers of one account can, at the same moment,
/// which the append allows for (<see cref="IFileSystem.Append"/>). The layer knows nothing of what a line says. A line
/// is appended straight to the share, never through the local copy, and the lines of a span are read from the share. Anyone
/// who writes the share can change or delete them: the audit is for tracing only (design 12).
/// </summary>
public sealed partial class DataFolder
{
    /// <summary>
    /// The longest line of the audit in bytes, without its <c>\n</c>. A view is about 200 bytes with the name of its record,
    /// which the caller cuts to fit.
    /// </summary>
    public const int MaxAuditLine = 4096;

    /// <summary>
    /// The bytes of a file of the audit that a read takes, 1 MiB: some 4000 views of one account in one day. The whole lines
    /// within it are read, and a larger file is reported (<see cref="SkipReason.OverBudget"/>).
    /// </summary>
    public const int MaxAuditFile = 1024 * 1024;

    /// <summary>The days of one read: a span of 31 days, which the audit screen takes at most, ends on its 32nd date in UTC.</summary>
    public const int MaxAuditDays = 32;

    /// <summary>
    /// The memory that one read of the audit takes, as much as the budget of the local copy (<see cref="DataCopy.MaxBytes"/>):
    /// the bytes of its files and <see cref="AuditLineCost"/> for each line, as a file of empty lines would otherwise take
    /// some 16 times its bytes (security review of M6.17).
    /// </summary>
    internal const long MaxAuditBytes = 64L * 1024 * 1024;

    /// <summary>
    /// The files that one read of the audit opens, as anyone who writes the share can put up to the listing cap of a load in
    /// each day folder, and a read holds the lock of the local copy (security review of M6.17): a team of 100 over 32 days has
    /// 3200.
    /// </summary>
    internal const int MaxAuditFiles = 10_000;

    /// <summary>What a line costs the budget of a read besides its bytes: a <see cref="ReadOnlyMemory{T}"/> in the list of its file.</summary>
    private const int AuditLineCost = 16;

    /// <summary>The ending of a file of the audit: JSON Lines.</summary>
    internal const string AuditExtension = ".jsonl";

    /// <summary>
    /// Appends a line to the audit of an account for a day: the layer adds the <c>\n</c>. The root and <c>audit\</c> are held
    /// as a write holds them, and the day folder is made and held as itself (M2.5). The file is opened as itself only to
    /// append, made when it is missing, and the line goes at its end in one write, after the held folders are checked
    /// (<see cref="IFileSystem.OpenToAppend"/>, <see cref="IFileSystem.Append"/>). An account that fails
    /// <see cref="StorageName"/>, an empty line, one over <see cref="MaxAuditLine"/> or one with a <c>\n</c> is an
    /// <see cref="ArgumentException"/>, before anything is made. A missing <c>audit\</c> is a
    /// <see cref="DirectoryNotFoundException"/>, a link at any of them an <see cref="IOException"/>.
    /// </summary>
    internal void AppendAudit(DateOnly day, string account, ReadOnlySpan<byte> line)
    {
        StorageName.Check(account);
        if (line.IsEmpty || line.Length > MaxAuditLine || line.Contains((byte)'\n'))
        {
            throw new ArgumentException($"A line of the audit has 1 to {MaxAuditLine} bytes and no line end.", nameof(line));
        }

        ThrowIfPrivate();
        byte[] bytes = [.. line, (byte)'\n'];
        var dayFolder = Path.Combine(Root, Audit, DayName(day));
        using var folders = Hold(create: false, Audit);
        HoldBelow(folders, dayFolder, create: true);
        using var file = Files.OpenToAppend(Path.Combine(dayFolder, account + AuditExtension));

        // An empty folder made a mount point in place would have given the file of that name elsewhere.
        folders.ThrowIfLink();
        Files.Append(file, bytes);
    }

    /// <summary>
    /// The lines of the audit of the days from <paramref name="first"/> to <paramref name="last"/>, both included, at most
    /// <see cref="MaxAuditDays"/> of them, day by day and account by account, each line without its <c>\n</c>. The root and
    /// <c>audit\</c> are held for the whole read and each day folder while it is read, as a load holds its folders, and checked
    /// through their handles after the reads: a day folder that has become a link gives nothing and is reported, and a root or
    /// <c>audit\</c> that has is an <see cref="IOException"/>, as for a load. Each file is read as itself, at most
    /// <see cref="MaxAuditFile"/> bytes of it, whole lines only, so a line that another Kiler is writing is left out; at most
    /// <paramref name="maxFiles"/> files are opened, and <paramref name="maxBytes"/> of memory taken, the bytes of a file
    /// before it is read and <see cref="AuditLineCost"/> for each of its lines. What is not read is reported each for itself
    /// and does not stop the read: <c>audit\</c> or a day folder that is a link or cannot be held or listed, or lists more
    /// than <paramref name="maxEntries"/> entries; a file that is a link, has a bad name or cannot be read; a file over the
    /// cap, whose lines within it are given; and the files over <paramref name="maxFiles"/> or <paramref name="maxBytes"/>.
    /// At most <paramref name="maxReported"/> are kept, and the rest counted. A missing <c>audit\</c> or day folder gives
    /// nothing. A root that cannot be held is an <see cref="IOException"/> or an <see cref="UnauthorizedAccessException"/>, as
    /// for a load; a span backwards or of more days an <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    internal AuditRead ReadAudit(DateOnly first, DateOnly last, int maxEntries, int maxReported, long maxBytes, int maxFiles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(last, first);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(last.DayNumber - first.DayNumber, MaxAuditDays, nameof(last));
        ThrowIfPrivate();
        var read = new AuditReading(maxReported, maxBytes, maxFiles);
        using var folders = Hold(create: false);
        var audit = Path.Combine(Root, Audit);
        if (HoldAuditFolder(folders, audit, Audit, read))
        {
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                ReadAuditDay(day, audit, maxEntries, read);
            }

            // An empty folder made a mount point in place would have given the days of another folder.
            folders.ThrowIfLink();
        }

        return new AuditRead(read.Files, read.Skipped, read.MoreSkipped);
    }

    /// <summary>The name of the folder of a day, <c>yyyy-MM-dd</c>, which passes <see cref="StorageName"/>.</summary>
    private static string DayName(DateOnly day) => StorageName.Check(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>
    /// Reads the files of a day folder: its listing, then the files with a good name, at most <see cref="LoadReads"/> at a
    /// time, then the check of the day folder through its handle. A day folder that became a link before the check gives
    /// nothing and is reported.
    /// </summary>
    private void ReadAuditDay(DateOnly day, string audit, int maxEntries, AuditReading read)
    {
        var name = DayName(day);
        var path = Path.Combine(audit, name);
        var shown = Path.Combine(Audit, name);
        using var held = new Holds();
        if (!HoldAuditFolder(held, path, shown, read))
        {
            return;
        }

        IReadOnlyList<FileEntry> entries;
        try
        {
            entries = Files.List(path, maxEntries + 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            read.Skip(shown, null, SkipReason.Unreadable);
            return;
        }

        if (entries.Count > maxEntries)
        {
            read.Skip(shown, null, SkipReason.OverBudget);
            return;
        }

        var accounts = new List<string>();
        foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            if (entry.IsLink)
            {
                read.Skip(shown, entry.Name, SkipReason.Link);
            }
            else if (!entry.IsFolder && entry.Name.EndsWith(AuditExtension, StringComparison.OrdinalIgnoreCase))
            {
                var account = entry.Name[..^AuditExtension.Length];
                if (entry.Name.EndsWith(AuditExtension, StringComparison.Ordinal) && StorageName.IsValid(account))
                {
                    accounts.Add(account);
                }
                else
                {
                    read.Skip(shown, entry.Name, SkipReason.BadName);
                }
            }
        }

        var files = Workers.Run(accounts, LoadReads, account => ReadAuditFile(Path.Combine(path, account + AuditExtension), read));
        try
        {
            held.ThrowIfLink();
        }
        catch (IOException)
        {
            read.Skip(shown, null, SkipReason.Link);
            return;
        }

        for (var i = 0; i < accounts.Count; i++)
        {
            var (lines, skipped) = files[i];
            if (skipped is { } reason)
            {
                read.Skip(shown, accounts[i] + AuditExtension, reason);
            }

            if (lines is not null)
            {
                read.Files.Add(new AuditFile(day, accounts[i], lines));
            }
        }
    }

    /// <summary>
    /// Reads a file of the audit, opened as itself, once the read may open one more: at most <see cref="MaxAuditFile"/> bytes,
    /// taken from the budget of the read before they are read, and the whole lines within them, each of which takes
    /// <see cref="AuditLineCost"/> more. Gives the lines, or <c>null</c> when nothing was read, and why it is reported:
    /// <see cref="SkipReason.OverBudget"/> with the lines of a file over the cap, or without lines when the read may open no
    /// more files or its budget has no room; <see cref="SkipReason.Unreadable"/> for a file that cannot be opened or read. A
    /// file that has gone since the listing gives nothing.
    /// </summary>
    private (List<ReadOnlyMemory<byte>>? Lines, SkipReason? Skipped) ReadAuditFile(string path, AuditReading read)
    {
        if (!read.TryOpen())
        {
            return (null, SkipReason.OverBudget);
        }

        byte[] buffer;
        int length;
        bool over;
        try
        {
            using var stream = Files.OpenRead(path);
            if (stream is null)
            {
                return (null, null);
            }

            var size = Math.Min(stream.Length, MaxAuditFile);
            over = stream.Length > MaxAuditFile;
            if (!read.TryTake(size))
            {
                return (null, SkipReason.OverBudget);
            }

            buffer = new byte[size];
            length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, SkipReason.Unreadable);
        }

        // The bytes after the last line end are a line that is being written, or one that the cap cut.
        var rest = buffer.AsMemory(0, buffer.AsSpan(0, length).LastIndexOf((byte)'\n') + 1);
        var count = rest.Span.Count((byte)'\n');
        if (!read.TryTake((long)count * AuditLineCost))
        {
            return (null, SkipReason.OverBudget);
        }

        var lines = new List<ReadOnlyMemory<byte>>(count);
        while (!rest.IsEmpty)
        {
            var end = rest.Span.IndexOf((byte)'\n');
            lines.Add(rest[..end]);
            rest = rest[(end + 1)..];
        }

        return (lines, over ? SkipReason.OverBudget : null);
    }

    /// <summary>
    /// Holds a folder of the audit below those held, or gives <c>false</c>: nothing when it is missing, and a report when it is
    /// a link, or anything else that cannot be held, such as a file at its name.
    /// </summary>
    private bool HoldAuditFolder(Holds holds, string path, string shown, AuditReading read)
    {
        try
        {
            HoldBelow(holds, path, create: false);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            read.Skip(shown, null, IsLinkAt(path) ? SkipReason.Link : SkipReason.Unreadable);
            return false;
        }
    }

    /// <summary>Whether a link is at the path now; <c>false</c> when it cannot be looked at.</summary>
    private bool IsLinkAt(string path)
    {
        try
        {
            return Files.Attributes(path) is { } attributes && attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The audit is in the data folder only.</summary>
    private void ThrowIfPrivate()
    {
        if (IsPrivate)
        {
            throw new InvalidOperationException("The audit is in the data folder, not in a private folder.");
        }
    }

    /// <summary>
    /// What a read of the audit has given so far: the files, the reports within their cap, and what is left of its budget, the
    /// files it may open and the memory it may take.
    /// </summary>
    private sealed class AuditReading(int maxReported, long maxBytes, int maxFiles)
    {
        private long _left = maxBytes;
        private int _opened;

        public List<AuditFile> Files { get; } = [];

        public List<SkippedEntry> Skipped { get; } = [];

        public int MoreSkipped { get; private set; }

        /// <summary>Counts a file that the read opens, from any thread; <c>false</c> once it may open no more.</summary>
        public bool TryOpen() => Interlocked.Increment(ref _opened) <= maxFiles;

        /// <summary>Takes bytes from the budget, from any thread; <c>false</c>, and nothing taken, when there is not that much left.</summary>
        public bool TryTake(long bytes)
        {
            var left = Volatile.Read(ref _left);
            while (left >= bytes)
            {
                var seen = Interlocked.CompareExchange(ref _left, left - bytes, left);
                if (seen == left)
                {
                    return true;
                }

                left = seen;
            }

            return false;
        }

        /// <summary>Reports an entry, with its raw name escaped (<see cref="StorageName.Escape"/>); over the cap, only counts it.</summary>
        public void Skip(string folder, string? name, SkipReason reason)
        {
            if (Skipped.Count < maxReported)
            {
                Skipped.Add(new SkippedEntry(folder, name is null ? null : StorageName.Escape(name), reason));
            }
            else
            {
                MoreSkipped++;
            }
        }
    }
}

/// <summary>
/// What a read of the audit gave (M6.17, <see cref="DataCopy.ReadAudit"/>): the lines of each file of the days of its span,
/// and what was skipped, to be reported.
/// </summary>
public sealed class AuditRead
{
    internal AuditRead(IReadOnlyList<AuditFile> files, IReadOnlyList<SkippedEntry> skipped, int moreSkipped)
    {
        Files = files;
        Skipped = skipped;
        MoreSkipped = moreSkipped;
    }

    /// <summary>The files that were read, day by day and account by account, in the order of their names.</summary>
    public IReadOnlyList<AuditFile> Files { get; }

    /// <summary>
    /// What was not read, or not whole: <c>audit</c> or a day folder, such as <c>audit\2026-10-31</c>, with no name, and a file
    /// of a day folder by its escaped name. At most the reports of a load (<see cref="CopyLimits"/>); <see cref="MoreSkipped"/>
    /// counts the rest.
    /// </summary>
    public IReadOnlyList<SkippedEntry> Skipped { get; }

    /// <summary>How many skipped entries there were beyond those in <see cref="Skipped"/>.</summary>
    public int MoreSkipped { get; }
}

/// <summary>
/// A file of the audit that a read took: its day, its account from its name, and its whole lines without their <c>\n</c>, as
/// they are. A line can say anything, as anyone who writes the share can write it.
/// </summary>
public sealed record AuditFile(DateOnly Day, string Account, IReadOnlyList<ReadOnlyMemory<byte>> Lines);
