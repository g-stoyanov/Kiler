using Kiler.Core;

namespace Kiler.DemoData;

/// <summary>
/// The stashes and the records of the demo (M5.5, PLAN M8 step 4): the notes of a programmer, two projects of work with
/// their environments, the home, a photo blog of their own and the team's handbook, in some 36 records of the five initial
/// templates, with tags, at the levels 0 to 3, some by the demo editor; and a volume mode of <see cref="VolumeCount"/>
/// records in all, for the timing of the search (M5.6). They are written through the code of an editor
/// (<see cref="RecordActions"/>), so each field takes the level of its template and each group is encrypted as Kiler does
/// it. Every host is under <c>example.com</c>, and every password is made up (CLAUDE.md, "Data and the public repo").
/// </summary>
internal static class DemoRecords
{
    /// <summary>How many records the volume mode makes in all: the demo records and made-up ones of more systems.</summary>
    public const int VolumeCount = 2000;

    /// <summary>The systems of the volume mode, each a stash with the environments of the demo under it.</summary>
    public const int VolumeSystems = 40;

    private const string Live = "Live";
    private const string Sandbox = "Sandbox";
    private const string Local = "Local";

    private static readonly string Admin = DemoKiler.Admin.Account;
    private static readonly string Editor = DemoKiler.Editor.Account;

    /// <summary>The words of the names of the volume mode, so the search meets each in many records.</summary>
    private static readonly string[] Words = ["Book", "Workout", "Photo", "Recipe", "Trip", "Garden", "Concert", "Course", "Budget", "Router"];

    private static readonly string[] Kinds = ["documentation", "logs", "monitoring", "other"];

    private static readonly Dictionary<string, int> NoLevels = [];

    private static readonly Dictionary<string, string> NoRefs = [];

    /// <summary>The stashes of the demo, each with the account that makes it.</summary>
    public static IReadOnlyList<DemoStash> Stashes { get; } =
    [
        new("Library app", Admin),
        new("Fitness app", Admin),
        new("Home", Admin),
        new("Photo blog", Admin),
        new("Handbook", Admin),
        new(Live, Admin),
        new(Sandbox, Admin),
        new(Local, Admin),
        new("Reports", Admin),
        new("Payments", Admin),
        new("Integrations", Editor),
    ];

    /// <summary>
    /// The records of the demo, in the order that they are made for each author, so a <c>ref</c> names a record made before
    /// it. Their names are unique, as a <c>ref</c> names its record by its name here.
    /// </summary>
    public static IReadOnlyList<DemoRecord> Set { get; } =
    [
        // Library app, a project of work: its PostgreSQL in each environment, its reports, its documentation and its logs.
        Db(Admin, ["Library app", Live], "Library database", "prod", "library-db.example.com", 5432, "library_app", "Demo-Library-Live-7Q",
            ["postgresql", "library"], description: "The lending system of the city library, read and write."),
        Db(Editor, ["Library app", Sandbox], "Library sandbox database", "test", "library-db-sandbox.example.com", 5432, "library_app", "Demo-Library-Sandbox-3K",
            ["postgresql", "library"]),
        Db(Editor, ["Library app", Local], "Library local database", "dev", "library-db-local.example.com", 5432, "library_dev", "Demo-Library-Local-5M",
            ["postgresql", "library"], description: "Refreshed from the sandbox every Monday."),
        Sql(Admin, ["Library app", "Reports"], "Overdue loans by branch", "Library database",
            """
            SELECT b.title, m.card_no, l.due_on, CURRENT_DATE - l.due_on AS days_late
              FROM loans l
              JOIN books b ON b.id = l.book_id
              JOIN members m ON m.id = l.member_id
             WHERE l.returned_on IS NULL
               AND l.due_on < CURRENT_DATE
               AND l.branch_id = :branch
             ORDER BY days_late DESC
            """,
            ["reporting", "monthly"], notes: "Run it on the first working day of the month, once for each branch."),
        Sql(Editor, ["Library app", "Reports"], "Reservations waiting over a week", "Library sandbox database",
            """
            SELECT r.id, b.title, r.reserved_on
              FROM reservations r
              JOIN books b ON b.id = r.book_id
             WHERE r.status = 'WAITING'
               AND r.reserved_on < CURRENT_DATE - 7
            """,
            ["reporting", "reservations"], description: "Reservations that waited for a free copy for more than a week."),
        Link(Admin, ["Library app"], "Library app documentation", "https://docs.example.com/library", "documentation", ["docs", "library"]),
        Link(Admin, ["Library app", Live], "Library app logs", "https://logs.example.com/app/library", "logs", ["logs", "library"],
            notes: "Filter by the card number; the logs are kept for 14 days."),
        Note(Admin, ["Library app"], "Monthly report to the library",
            """
            1. Run "Overdue loans by branch" for each branch.
            2. Put the results into the report and send it to the library.
            3. Close the ticket of the month once the library confirms.
            """,
            ["runbook", "monthly"]),

        // Fitness app, another project of work: a MySQL, its monitoring, its API and the steps of an outage, kept at the top level.
        Db(Admin, ["Fitness app", Live], "Fitness database", "prod", "fitness-db.example.com", 3306, "fitness_app", "Demo-Fitness-Live-9W",
            ["mysql", "fitness"]),
        Db(Editor, ["Fitness app", Sandbox], "Fitness sandbox database", "test", "fitness-db-sandbox.example.com", 3306, "fitness_app", "Demo-Fitness-Sandbox-2H",
            ["mysql", "fitness"]),
        Sql(Admin, ["Fitness app", "Reports"], "Workouts logged today", "Fitness database",
            """
            SELECT w.kind, COUNT(*) AS workouts, ROUND(AVG(w.minutes)) AS avg_minutes
              FROM workouts w
             WHERE w.logged_at >= CURRENT_DATE
             GROUP BY w.kind
            """,
            ["reporting", "daily"]),
        Sql(Admin, ["Fitness app", Live], "Long-running sessions", "Fitness database",
            """
            SELECT id, user, host, time AS seconds, info
              FROM information_schema.processlist
             WHERE command <> 'Sleep' AND time > 300
            """,
            ["mysql", "performance"], notes: "Ask in the team chat before you end a session."),
        Link(Admin, ["Fitness app", Live], "Fitness app monitoring", "https://monitoring.example.com/d/fitness", "monitoring", ["monitoring", "fitness"]),
        Link(Editor, ["Fitness app"], "Fitness app API reference", "https://docs.example.com/fitness/api", "documentation", ["docs", "api"]),
        Note(Admin, ["Fitness app", Live], "Fitness app outage steps",
            """
            ## When the app cannot sync the workouts

            1. Check "Fitness app monitoring" for the failing step.
            2. Restart the sync workers from the cloud console.
            3. If the database is the cause, call the database team on call.
            """,
            ["runbook", "on-call"], description: "Steps for an outage of the fitness app.", levels: new() { ["text"] = 3 }),
        Snippet(Admin, ["Fitness app", Live], "Clean up the old sync logs", "bash",
            """
            #!/usr/bin/env bash
            # Deletes the logs of the sync workers older than 14 days, then shows how much is left.
            set -euo pipefail
            logs=/var/log/fitness-sync
            find "$logs" -name '*.log' -mtime +14 -print -delete
            du -sh "$logs"
            """,
            ["logs", "maintenance"], notes: "Run it as the user of the sync workers when the disk is over 80% full."),
        Snippet(Editor, ["Fitness app", Sandbox], "Fitness app settings for the sandbox", "json",
            """
            {
              "api": "https://fitness-api-sandbox.example.com/v2",
              "syncEveryMinutes": 15,
              "units": "metric",
              "features": {
                "heartRate": true,
                "challenges": false
              },
              "logLevel": "debug"
            }
            """,
            ["settings", "fitness"], notes: "The challenges stay off in the sandbox until the next release."),

        // Home: the home server with the database of the budget, the router, the logs of the smart home three stashes deep,
        // and the printer.
        Db(Admin, ["Home", Live], "Home server", "prod", "home.example.com", 5432, "budget", "Demo-Home-Live-4T",
            ["postgresql", "home"]),
        Db(Editor, ["Home", Local], "Wi-Fi router", "dev", "router.example.com", 443, "admin", "Demo-Router-Local-8P",
            ["network", "home"]),
        Sql(Admin, ["Home", "Reports"], "Spending by category this month", "Home server",
            """
            SELECT c.name AS category, SUM(e.amount) AS spent
              FROM expenses e
              JOIN categories c ON c.id = e.category_id
             WHERE e.spent_on >= DATE_TRUNC('month', CURRENT_DATE)
             GROUP BY c.name
             ORDER BY spent DESC
            """,
            ["reporting", "budget"]),
        Link(Editor, ["Home", "Integrations", Sandbox], "Smart home logs", "https://logs.example.com/app/smart-home-sandbox", "logs",
            ["logs", "smart-home"]),
        Note(Editor, ["Home"], "Printer setup",
            """
            Add the printer from Settings › Printers, then print the test page. The spare ink is in the hall cupboard.
            """,
            ["hardware"]),
        Snippet(Admin, ["Home"], "Back up the photos", "powershell",
            """
            # Copies the new and changed photos to the home server.
            $source = Join-Path $env:USERPROFILE 'Pictures'
            $target = '\\home.example.com\backup\photos'
            robocopy $source $target /E /XO /R:2 /W:5 /LOG:"$env:TEMP\photo-backup.log"
            if ($LASTEXITCODE -ge 8) {
                Write-Error "The backup failed; see $env:TEMP\photo-backup.log"
            }
            """,
            ["backup", "photos"], description: "Run it on Sunday evening."),

        // Photo blog: its hosting, and a payment provider four stashes deep for the prints, its notes raised.
        Db(Admin, ["Photo blog", Live], "Photo blog hosting", "prod", "sftp.example.com", 22, "photoblog", "Demo-Blog-Live-6V",
            ["sftp", "blog"]),
        Link(Admin, ["Photo blog", "Integrations", "Payments", Sandbox], "Payment provider sandbox", "https://sandbox.payments.example.com", "documentation",
            ["payments", "sandbox"], notes: "The sandbox takes the test cards of the provider's documentation only.", levels: new() { ["notes"] = 2 }),
        Link(Admin, ["Photo blog", "Integrations", "Payments", Live], "Payment provider dashboard", "https://dashboard.payments.example.com", "monitoring",
            ["payments", "monitoring"], notes: "The prints sold and the payouts; the second factor is on my phone.", levels: new() { ["notes"] = 3 }),
        Link(Editor, ["Photo blog"], "Photo blog uptime check", "https://status.example.com/photo-blog", "monitoring", ["monitoring", "blog"]),
        Note(Editor, ["Photo blog"], "New gallery checklist",
            """
            - [ ] The photos are resized, and their location is removed.
            - [ ] Each photo has a title and a text for screen readers.
            - [ ] The prints of the gallery are in the shop.
            """,
            ["publishing"]),
        Snippet(Editor, ["Photo blog"], "Resize the photos for the blog", "python",
            """
            from pathlib import Path

            from PIL import Image

            # A copy of each photo of the gallery, at most 1600 px on its longest side, without its location.
            web = Path("web")
            web.mkdir(exist_ok=True)
            for photo in sorted(Path("gallery").glob("*.jpg")):
                with Image.open(photo) as image:
                    image.thumbnail((1600, 1600))
                    image.save(web / photo.name, quality=85)
                print(f"{photo.name}: done")
            """,
            ["photos", "publishing"], notes: "Needs Pillow: pip install Pillow.", description: "The copies for the web of a new gallery; the originals stay as they are."),
        Snippet(Admin, ["Photo blog", Live], "Photo blog build pipeline", "yaml",
            """
            # Builds the blog and uploads it to the hosting at each push to main.
            name: publish
            on:
              push:
                branches: [main]
            jobs:
              publish:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                  - run: npm ci && npm run build
                  - name: Upload
                    run: rsync -az --delete public/ photoblog@sftp.example.com:/var/www/blog
            """,
            ["build", "blog"], notes: "The key of the upload is a secret of the repository, never in the pipeline."),

        // Handbook: what the whole team needs.
        Note(Admin, ["Handbook"], "Support rota",
            """
            | Week | Primary | Secondary |
            |---|---|---|
            | 44 | Demo Admin | Demo Editor |
            | 45 | Demo Editor | Demo Reader |
            """,
            ["on-call"]),
        Note(Admin, ["Handbook"], "Welcome to the team",
            """
            Start with the Team wiki, then ask for your Kiler account and level.
            """,
            ["onboarding"], description: "Read this first."),
        Link(Admin, ["Handbook"], "Team wiki", "https://wiki.example.com/team", "documentation", ["docs", "onboarding"]),
        Link(Admin, ["Handbook"], "Central logs", "https://logs.example.com", "logs", ["logs"]),
        Link(Admin, ["Handbook"], "Holiday calendar", "https://calendar.example.com/team", "other", ["calendar"]),
        Note(Editor, ["Handbook"], "Access requests",
            """
            Ask for access to a sandbox database in the service desk, with the name of the record in Kiler.
            """,
            ["access"], description: "Who grants access to the sandbox databases.", levels: new() { ["description"] = 2 }),
    ];

    /// <summary>How many records <see cref="Write"/> makes.</summary>
    public static int CountOf(bool volume) => volume ? VolumeCount : Set.Count;

    /// <summary>
    /// Makes the stashes and the records, through <paramref name="records"/> and the templates of <paramref name="files"/>, each
    /// by the session of its author among <paramref name="authors"/>; with <paramref name="volume"/>, the made-up records of
    /// <see cref="VolumeSystems"/> more systems too, by the admin, to <see cref="VolumeCount"/> records in all. The records of one
    /// author are made together, as <see cref="Records.Read"/> decrypts the whole set again at each new author.
    /// </summary>
    /// <exception cref="InvalidOperationException">A stash or a record was not made; the message says which.</exception>
    public static void Write(Records records, TeamFiles files, IReadOnlyList<Session> authors, bool volume, TimeProvider time)
    {
        var stashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var volumeStashes = volume ? Enumerable.Range(1, VolumeSystems).Select(SystemName).Select(name => new DemoStash(name, Admin)) : [];
        foreach (var stash in Stashes.Concat(volumeStashes).OrderBy(stash => AuthorIndex(authors, stash.Author)))
        {
            var made = Made(RecordActions.NewStash(Author(authors, stash.Author), records, stash.Name, time), stash.Name);
            stashes.Add(stash.Name, made.Stash?.Id ?? throw new InvalidOperationException($"The stash {stash.Name} was made, but not given back."));
        }

        var volumeRecords = volume ? Volume(VolumeCount - Set.Count) : [];
        foreach (var record in Set.Concat(volumeRecords).OrderBy(record => AuthorIndex(authors, record.Author)))
        {
            // A ref is the id of the record that it names, made before it.
            var fields = record.Fields.ToDictionary(pair => pair.Key, pair => (FieldValue?)pair.Value, StringComparer.Ordinal);
            foreach (var (key, target) in record.Refs)
            {
                fields.Add(key, FieldValue.OfText(ids[target]));
            }

            var draft = new RecordDraft(record.Template, [.. record.Path.Select(name => stashes[name])], record.Name, record.Description, record.Tags, fields, record.Levels);
            var made = Made(RecordActions.NewRecord(Author(authors, record.Author), records, files, draft, time), record.Name);
            ids.Add(record.Name, made.Record?.File.Model?.Id ?? throw new InvalidOperationException($"The record {record.Name} was made, but not read back."));
        }
    }

    /// <summary>The name of the volume system <paramref name="number"/>, from 1.</summary>
    public static string SystemName(int number) => $"System {number:00}";

    /// <summary>
    /// The made-up records of the volume mode, in groups of four templates: a database, a query on it, a link and a note
    /// of one word, system and environment, so the search, the filters and the tree have many of each. The password of a
    /// database is at the level of its environment, and the note of each group rises from 1 to 3.
    /// </summary>
    private static IEnumerable<DemoRecord> Volume(int count)
    {
        string[] envs = ["prod", "test", "dev"];
        string[] envStashes = [Live, Sandbox, Local];
        for (var i = 0; i < count; i++)
        {
            var group = i / 4;
            var word = Words[group % Words.Length];
            var system = group % VolumeSystems + 1;
            var env = group / VolumeSystems % envs.Length;
            string[] path = [SystemName(system), envStashes[env]];
            string[] tags = [word.ToLowerInvariant(), envs[env]];
            var db = $"{word} database {group:0000}";
            yield return (i % 4) switch
            {
                0 => Db(Admin, path, db, envs[env], $"{word.ToLowerInvariant()}-{group:0000}-{envs[env]}.example.com", 5432,
                    $"{word.ToLowerInvariant()}_app", $"Demo-Volume-{group:0000}", tags),
                1 => Sql(Admin, path, $"{word} query {group:0000}", db, $"SELECT * FROM {word.ToLowerInvariant()}s WHERE id = :id", tags),
                2 => Link(Admin, path, $"{word} link {group:0000}", $"https://docs.example.com/system-{system:00}/{group:0000}", Kinds[group % Kinds.Length], tags),
                _ => Note(Admin, path, $"{word} note {group:0000}", $"Notes on the {word.ToLowerInvariant()}s of system {system:00}.", tags,
                    levels: new() { ["text"] = 1 + (group % 3) }),
            };
        }
    }

    private static int AuthorIndex(IReadOnlyList<Session> authors, string account)
    {
        for (var i = 0; i < authors.Count; i++)
        {
            if (authors[i].Account == account)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"{account} is not among the authors.");
    }

    private static Session Author(IReadOnlyList<Session> authors, string account) => authors[AuthorIndex(authors, account)];

    private static RecordResult Made(RecordResult result, string name) =>
        result.Outcome == RecordOutcome.Done ? result : throw new InvalidOperationException($"{name} was not made ({result.Outcome}): {result.Invalid}");

    /// <summary>A record of the template <c>db</c> ("Server / database").</summary>
    private static DemoRecord Db(
        string author, string[] path, string name, string env, string host, int port, string user, string password, string[] tags,
        string? description = null) =>
        new(author, "db", path, name, tags, Values(("env", env), ("host", host), ("port", FieldValue.OfNumber(port)), ("user", user), ("password", password)),
            description, NoLevels, NoRefs);

    /// <summary>A record of the template <c>sql</c> ("SQL query"), which runs in the record named <paramref name="runsIn"/>.</summary>
    private static DemoRecord Sql(string author, string[] path, string name, string runsIn, string query, string[] tags, string? notes = null, string? description = null) =>
        new(author, "sql", path, name, tags, Values(("query", query), ("notes", notes)), description, NoLevels, new Dictionary<string, string> { ["db"] = runsIn });

    /// <summary>A record of the template <c>link</c>.</summary>
    private static DemoRecord Link(
        string author, string[] path, string name, string url, string kind, string[] tags, string? notes = null, Dictionary<string, int>? levels = null) =>
        new(author, "link", path, name, tags, Values(("url", url), ("kind", kind), ("notes", notes)), null, levels ?? NoLevels, NoRefs);

    /// <summary>A record of the template <c>note</c>.</summary>
    private static DemoRecord Note(string author, string[] path, string name, string text, string[] tags, string? description = null, Dictionary<string, int>? levels = null) =>
        new(author, "note", path, name, tags, Values(("text", text)), description, levels ?? NoLevels, NoRefs);

    /// <summary>A record of the template <c>snippet</c>, whose code is in <paramref name="language"/>.</summary>
    private static DemoRecord Snippet(
        string author, string[] path, string name, string language, string code, string[] tags, string? notes = null, string? description = null) =>
        new(author, "snippet", path, name, tags, Values(("code", FieldValue.OfCode(language, code)), ("notes", notes)), description, NoLevels, NoRefs);

    /// <summary>The values by key: a text, or a <see cref="FieldValue"/>; a <c>null</c> is no value and is left out.</summary>
    private static Dictionary<string, FieldValue> Values(params (string Key, object? Value)[] values) =>
        values.Where(pair => pair.Value is not null).ToDictionary(
            pair => pair.Key,
            pair => pair.Value as FieldValue ?? FieldValue.OfText((string)pair.Value!),
            StringComparer.Ordinal);
}

/// <summary>A stash of the demo, by its name, and the account that makes it.</summary>
internal sealed record DemoStash(string Name, string Author);

/// <summary>
/// A record of the demo: its author, its template, its path by the names of its stashes, its name and tags, the values of its
/// fields by key, and the levels asked (<see cref="RecordDraft.Levels"/>). A <c>ref</c> names the record that it points to by
/// its name, in <see cref="Refs"/>, as its id is known only once that record is made.
/// </summary>
internal sealed record DemoRecord(
    string Author,
    string Template,
    IReadOnlyList<string> Path,
    string Name,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, FieldValue> Fields,
    string? Description,
    IReadOnlyDictionary<string, int> Levels,
    IReadOnlyDictionary<string, string> Refs)
{
    /// <summary>The name: a record prints its properties, and this one holds a demo password (SKILL.md, "Style").</summary>
    public override string ToString() => Name;
}
