using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// The measures of M2.11c that need the internals of the layer: <c>Versions</c> with 100 slots on the file server of the shared
/// folder, with the writes that made them; a write through a local copy whose index is full; and what
/// <see cref="DocumentFormat.Read(DocumentKey, ReadOnlyMemory{byte})"/> pays for parsing a good file twice. The last two
/// touch no share, as the local copy is on the local disk, in the profile, and a parse is work of this machine; they are slow
/// or say little alone, so they run with the first. They run only when <see cref="TestFolder.Variable"/> names a folder on a file server, as
/// <see cref="FileServerTests"/> do, so they run again at the trial use in the real environment and after a move to another
/// server. They assert only that what they measure worked; the times go to the output of the test, which a run of the test
/// exe with <c>-class Kiler.Storage.Tests.FileServerMeasures -showLiveOutput</c> shows. The owners are the fake ones of the
/// tests, read by the id of the file through its handle; the measures with the owners of Windows are in
/// <c>Kiler.App.Tests.FileServerMeasures</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileServerMeasures : IDisposable
{
    private const string NotOnAFileServer = $"Runs only when {TestFolder.Variable} names a folder on a file server.";

    /// <summary>The documents of the user's own in the full index: the budget, less a few for the creates that are timed.</summary>
    private const int OwnDocuments = DataCopy.MaxDocuments - 100;

    private const int OtherUsers = DataCopy.MaxOtherDocuments / DataCopy.MaxDocumentsPerOther;

    /// <summary>How many writes of each kind are timed at each fill of the index; the median is reported.</summary>
    private const int TimedWrites = 5;

    /// <summary>The documents whose reads <see cref="Read_OfAGoodFile_ParsesItTwice_WhatTheSecondParseCosts"/> times, as the load of M2.11c.</summary>
    private const int Parsed = 2000;

    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");

    private readonly TestFolder _test = new();

    /// <summary>The versions of the documents that <see cref="TimeWrites"/> made or replaced.</summary>
    private readonly Dictionary<DocumentKey, int> _versions = [];

    /// <summary>How many new documents <see cref="TimeWrites"/> has made.</summary>
    private int _created;

    public static bool OnAFileServer => FileServerTests.OnAFileServer;

    public void Dispose() => _test.Dispose();

    /// <summary>
    /// <c>Versions</c> of a document with 100 slots, each opened as itself for its owner (M2.6, estimated at about a second from
    /// the reads of M1.0b), the first time and again; and the 100 writes that made the slots, each a write of the share alone,
    /// without a local copy.
    /// </summary>
    [Fact(Skip = NotOnAFileServer, SkipUnless = nameof(OnAFileServer))]
    public void Versions_Of100Slots_AndTheWritesThatMadeThem()
    {
        var share = DataFolder.Create(Path.Combine(_test.Root, "data"), new TestOwners());
        Assert.Equal(WriteOutcome.Written, share.CreateDocument(Record, Content("v1")).Outcome);
        var writes = new List<TimeSpan>();
        for (var version = 1; version <= DataFolder.SlotsWithOwners; version++)
        {
            var write = Stopwatch.StartNew();
            Assert.Equal(WriteOutcome.Written, share.ReplaceDocument(Record, version, Content($"v{version + 1}")).Outcome);
            writes.Add(write.Elapsed);
        }

        var clock = Stopwatch.StartNew();
        var versions = share.Versions(Record, DateTimeOffset.UtcNow);
        var first = clock.Elapsed;
        clock.Restart();
        var again = share.Versions(Record, DateTimeOffset.UtcNow);
        var second = clock.Elapsed;

        foreach (var listed in new[] { versions, again })
        {
            Assert.Equal(DataFolder.SlotsWithOwners + 1, listed.Count);
            Assert.All(listed, version => Assert.Equal(TestOwners.Me, version.Owner));
        }

        Report($"{DataFolder.SlotsWithOwners} writes of one document on the share: median {Median(writes)} ms, from {Ms(writes.Min())} to {Ms(writes.Max())} ms.");
        Report($"Versions with {DataFolder.SlotsWithOwners} slots and their owners: {Ms(first)} ms, and {Ms(second)} ms again.");
    }

    /// <summary>
    /// A write through the local copy writes its whole index, and a replace writes it twice (M2.7a): timed with an index of the
    /// user's own documents up to the budget, then with a slot of each in its history, then in the copy of an admin with the
    /// private folders of other users up to their budget too (M2.7c). The data folder is on the local disk as well, so the
    /// times are those of the copy; a write of the share adds what <see cref="Versions_Of100Slots_AndTheWritesThatMadeThem"/>
    /// measures.
    /// </summary>
    [Fact(Skip = NotOnAFileServer, SkipUnless = nameof(OnAFileServer))]
    public void AWriteThroughTheCopy_WithAFullIndex()
    {
        using var local = TestFolder.OnTheLocalDisk();
        var share = DataFolder.Create(Path.Combine(local.Root, "data"), new TestOwners());
        var keys = Enumerable.Range(0, OwnDocuments).Select(n => DocumentKey.InFolder(DataFolder.Records, $"r{n:D5}")).ToArray();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var copyFolder = Directory.CreateDirectory(Path.Combine(local.Root, "copy")).FullName;
        var copy = DataCopy.Open(share, copyFolder, clock, "demo.admin");
        var replaced = keys[..TimedWrites];

        // The documents that the later writes replace are made by the first, while the index is empty. Each round of writes
        // adds as many entries with its creates and as many slots with its replaces, so the index is checked to be as full
        // as the round says: a document that the load did not take would make the times those of a smaller index.
        Report(TimeWrites(copy, copyFolder, replaced, "an empty index", TimedWrites, TimedWrites, createReplaced: true));

        foreach (var key in keys[TimedWrites..])
        {
            TestFolder.WriteAsKiler(share.DocumentPath(key), DocumentFormat.New(key, Content(key.Id)));
        }

        Assert.Empty(copy.Load().Skipped);
        Report(TimeWrites(copy, copyFolder, replaced, "the user's own documents", OwnDocuments + TimedWrites, 2 * TimedWrites));

        // A new version of each on the share: the copy keeps the version it had as a slot of its own (M2.7a).
        foreach (var key in keys[TimedWrites..])
        {
            Assert.Equal(WriteOutcome.Written, share.ReplaceDocument(key, 1, Content(key.Id + " v2")).Outcome);
        }

        Assert.Empty(copy.Load().Skipped);
        Report(TimeWrites(copy, copyFolder, replaced, "a slot of each", OwnDocuments + (2 * TimedWrites), OwnDocuments + (2 * TimedWrites)));

        var accounts = Enumerable.Range(1, OtherUsers).Select(n => $"demo.user{n:D2}").ToArray();
        foreach (var account in accounts)
        {
            var other = share.CreatePrivate(account);
            for (var n = 0; n < DataCopy.MaxDocumentsPerOther; n++)
            {
                var key = DocumentKey.InFolder(DataFolder.Records, $"p{n:D4}").InPrivate(account);
                TestFolder.WriteAsKiler(other.DocumentPath(key), DocumentFormat.New(key, Content(key.Id)));
            }
        }

        copy.CopyOthers(accounts);
        Assert.Empty(copy.Load().Skipped);
        Report(TimeWrites(
            copy,
            copyFolder,
            replaced,
            "the private folders of other users",
            OwnDocuments + (3 * TimedWrites) + DataCopy.MaxOtherDocuments,
            OwnDocuments + (3 * TimedWrites)));
    }

    /// <summary>
    /// <see cref="DocumentFormat.Read(DocumentKey, ReadOnlyMemory{byte})"/> checks a whole file through a pooled
    /// <see cref="JsonDocument"/> for its head, then parses a good one again into nodes (M2.7c): the cost of the second parse of
    /// 2000 records, against the time of their load.
    /// </summary>
    [Fact(Skip = NotOnAFileServer, SkipUnless = nameof(OnAFileServer))]
    public void Read_OfAGoodFile_ParsesItTwice_WhatTheSecondParseCosts()
    {
        var files = Enumerable.Range(0, Parsed)
            .Select(n => DocumentKey.InFolder(DataFolder.Records, $"r{n:D4}"))
            .Select(key => (Key: key, Bytes: DocumentFormat.New(key, Content(key.Id))))
            .ToArray();
        var heads = TimeSpan.MaxValue;
        var reads = TimeSpan.MaxValue;

        // The best of a few rounds, after the first has warmed up the code.
        for (var round = 0; round < 5; round++)
        {
            var clock = Stopwatch.StartNew();
            var damaged = files.Count(file => DocumentFormat.ReadHead(file.Key, file.Bytes).Damage is not null);
            heads = Min(heads, clock.Elapsed);
            clock.Restart();
            damaged += files.Count(file => DocumentFormat.Read(file.Key, file.Bytes).IsDamaged);
            reads = Min(reads, clock.Elapsed);
            Assert.Equal(0, damaged);
        }

        Report($"{Parsed} records of {files[0].Bytes.Length} bytes: their heads {Ms(heads)} ms, their whole reads {Ms(reads)} ms, so the second parse costs {Ms(reads - heads)} ms.");
    }

    /// <summary>
    /// Times a create and a replace of each of <paramref name="replaced"/> through the copy, the create of a new document or,
    /// with <paramref name="createReplaced"/>, of the one replaced; checks that the index then has <paramref name="entries"/>
    /// entries and <paramref name="slots"/> slots, and says so with the times.
    /// </summary>
    private string TimeWrites(DataCopy copy, string copyFolder, DocumentKey[] replaced, string filled, int entries, int slots, bool createReplaced = false)
    {
        var creates = new List<TimeSpan>();
        var replaces = new List<TimeSpan>();
        foreach (var key in replaced)
        {
            var made = createReplaced ? key : DocumentKey.InFolder(DataFolder.Records, $"w{_created++:D3}");
            var write = Stopwatch.StartNew();
            var create = copy.Create(made, Content(made.Id));
            creates.Add(write.Elapsed);
            Assert.Equal(WriteOutcome.Written, create.Outcome);
            _versions[made] = 1;

            write.Restart();
            var replace = copy.Replace(key, _versions[key], Content(key.Id + " again"));
            replaces.Add(write.Elapsed);
            Assert.Equal(WriteOutcome.Written, replace.Outcome);
            _versions[key]++;
        }

        var path = Path.Combine(copyFolder, "index.json");
        var index = JsonSerializer.Deserialize(File.ReadAllBytes(path), StorageJsonContext.Default.CopyIndex)!;
        Assert.True(index.Files?.Count == entries, $"The index has {index.Files?.Count} entries, not {entries}: the times are not those of a full index.");
        Assert.True(index.History?.Count == slots, $"The index has {index.History?.Count} slots, not {slots}: the times are not those of a full index.");
        return $"With {filled}: {entries} entries and {slots} slots, an index of {new FileInfo(path).Length / 1024} KiB; "
            + $"a create through the copy median {Median(creates)} ms, a replace {Median(replaces)} ms.";
    }

    private static string Median(List<TimeSpan> times) => Ms(times.Order().ElementAt(times.Count / 2));

    private static string Ms(TimeSpan time) => $"{time.TotalMilliseconds:F0}";

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>A made-up record of about 1.6 KB, as the 2000 records of 2–4 MB of design 5.3.</summary>
    private static JsonObject Content(string name) => new()
    {
        ["template"] = "db",
        ["name"] = $"Demo record {name}",
        ["host"] = $"{name}.demo.test",
        ["port"] = 5432,
        ["account"] = "demo.user",
        ["notes"] = string.Concat(Enumerable.Repeat($"Made-up notes of {name}. ", 60)),
    };

    private static void Report(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);
}
