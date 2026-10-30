using Kiler.Core;

namespace Kiler.DemoData;

/// <summary>
/// The stashes and the records of the demo (M5.5, PLAN M8 step 4): a few made-up systems with their environments, and about
/// 30 records of the four initial templates, with tags, at the levels 0 to 3, some by the demo editor; and a volume mode of
/// <see cref="VolumeCount"/> records in all, for the timing of the search (M5.6). They are written through the code of an
/// editor (<see cref="RecordActions"/>), so each field takes the level of its template and each group is encrypted as Kiler
/// does it. Every host is under <c>example.com</c>, and every password is made up (CLAUDE.md, "Data and the public repo").
/// </summary>
internal static class DemoRecords
{
    /// <summary>How many records the volume mode makes in all: the demo records and made-up ones of more systems.</summary>
    public const int VolumeCount = 2000;

    /// <summary>The systems of the volume mode, each a stash with the environments of the demo under it.</summary>
    public const int VolumeSystems = 40;

    private const string Prod = "Prod";
    private const string Test = "Test";
    private const string Dev = "Dev";

    private static readonly string Admin = DemoKiler.Admin.Account;
    private static readonly string Editor = DemoKiler.Editor.Account;

    /// <summary>The words of the names of the volume mode, so the search meets each in many records.</summary>
    private static readonly string[] Words = ["Invoice", "Customer", "Stock", "Payment", "Shipment", "Ledger", "Contract", "Account", "Catalog", "Tariff"];

    private static readonly string[] Kinds = ["documentation", "logs", "monitoring", "other"];

    private static readonly Dictionary<string, int> NoLevels = [];

    private static readonly Dictionary<string, string> NoRefs = [];

    /// <summary>The stashes of the demo, each with the account that makes it.</summary>
    public static IReadOnlyList<DemoStash> Stashes { get; } =
    [
        new("Billing", Admin),
        new("Orders", Admin),
        new("Warehouse", Admin),
        new("Customer portal", Admin),
        new("Handbook", Admin),
        new(Prod, Admin),
        new(Test, Admin),
        new(Dev, Admin),
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
        // Billing: an Oracle database in each environment, its reports, its documentation and its logs.
        Db(Admin, ["Billing", Prod], "Billing prod database", "prod", "billing-db-prod.example.com", 1521, "billing_app", "Demo-Billing-Prod-7Q",
            ["oracle", "billing"], tns: Tns("billing-db-prod.example.com", "BILLPRD"), description: "The main database of billing, read and write."),
        Db(Editor, ["Billing", Test], "Billing test database", "test", "billing-db-test.example.com", 1521, "billing_app", "Demo-Billing-Test-3K",
            ["oracle", "billing"], tns: Tns("billing-db-test.example.com", "BILLTST")),
        Db(Editor, ["Billing", Dev], "Billing dev database", "dev", "billing-db-dev.example.com", 1521, "billing_dev", "Demo-Billing-Dev-5M",
            ["oracle", "billing"], description: "Refreshed from test every Monday."),
        Sql(Admin, ["Billing", "Reports"], "Unpaid invoices by month", "Billing prod database",
            """
            SELECT TRUNC(i.issued_on, 'MM') AS month, COUNT(*) AS invoices, SUM(i.amount) AS amount
              FROM invoices i
             WHERE i.paid_on IS NULL
             GROUP BY TRUNC(i.issued_on, 'MM')
             ORDER BY month DESC
            """,
            ["reporting", "monthly"], notes: "Run it on the **first working day** of the month, before the close."),
        Sql(Editor, ["Billing", "Reports"], "Invoices stuck in export", "Billing test database",
            """
            SELECT i.invoice_no, i.export_status, i.updated_on
              FROM invoices i
             WHERE i.export_status = 'PENDING'
               AND i.updated_on < SYSDATE - 1 / 24
            """,
            ["reporting", "export"], description: "Invoices that waited for the export for more than an hour."),
        Link(Admin, ["Billing"], "Billing documentation", "https://docs.example.com/billing", "documentation", ["docs", "billing"]),
        Link(Admin, ["Billing", Prod], "Billing logs", "https://logs.example.com/app/billing-prod", "logs", ["logs", "billing"],
            notes: "Filter by the invoice number; the logs are kept for 14 days."),
        Note(Admin, ["Billing"], "Billing month-end close",
            """
            1. Stop the export job at 18:00.
            2. Run *Unpaid invoices by month* and send it to finance.
            3. Start the export job again once finance confirms.
            """,
            ["runbook", "monthly"]),

        // Orders: a PostgreSQL database, its monitoring, its API and the runbook of an outage, kept at the top level.
        Db(Admin, ["Orders", Prod], "Orders prod database", "prod", "orders-db-prod.example.com", 5432, "orders_app", "Demo-Orders-Prod-9W",
            ["postgresql", "orders"]),
        Db(Editor, ["Orders", Test], "Orders test database", "test", "orders-db-test.example.com", 5432, "orders_app", "Demo-Orders-Test-2H",
            ["postgresql", "orders"]),
        Sql(Admin, ["Orders", "Reports"], "Orders by status today", "Orders prod database",
            """
            SELECT status, COUNT(*) AS orders
              FROM orders
             WHERE created_at >= CURRENT_DATE
             GROUP BY status
            """,
            ["reporting", "daily"]),
        Sql(Admin, ["Orders", Prod], "Long-running sessions", "Orders prod database",
            """
            SELECT pid, usename, state, NOW() - query_start AS running_for
              FROM pg_stat_activity
             WHERE state <> 'idle' AND NOW() - query_start > INTERVAL '5 minutes'
            """,
            ["postgresql", "performance"], notes: "Ask on the on-call channel before you end a session."),
        Link(Admin, ["Orders", Prod], "Orders monitoring", "https://monitoring.example.com/d/orders-prod", "monitoring", ["monitoring", "orders"]),
        Link(Editor, ["Orders"], "Orders API reference", "https://docs.example.com/orders/api", "documentation", ["docs", "api"]),
        Note(Admin, ["Orders", Prod], "Orders incident runbook",
            """
            ## When the orders service is down

            1. Check *Orders monitoring* for the failing step.
            2. Restart the order workers from the operations console.
            3. If the database is the cause, call the database team on call.
            """,
            ["runbook", "on-call"], description: "Steps for an outage of the orders service.", levels: new() { ["text"] = 3 }),

        // Warehouse: a database, the logs of an integration three stashes deep, a report and the setup of the scanners.
        Db(Admin, ["Warehouse", Prod], "Warehouse prod database", "prod", "wms-db-prod.example.com", 1521, "wms_app", "Demo-Warehouse-Prod-4T",
            ["oracle", "warehouse"], tns: Tns("wms-db-prod.example.com", "WMSPRD")),
        Db(Editor, ["Warehouse", Dev], "Warehouse dev database", "dev", "wms-db-dev.example.com", 1521, "wms_dev", "Demo-Warehouse-Dev-8P",
            ["oracle", "warehouse"]),
        Sql(Admin, ["Warehouse", "Reports"], "Stock below minimum", "Warehouse prod database",
            """
            SELECT s.sku, s.on_hand, s.minimum
              FROM stock s
             WHERE s.on_hand < s.minimum
             ORDER BY s.minimum - s.on_hand DESC
            """,
            ["reporting", "stock"]),
        Link(Editor, ["Warehouse", "Integrations", Test], "Warehouse integration logs", "https://logs.example.com/app/wms-integration-test", "logs",
            ["logs", "integration"]),
        Note(Editor, ["Warehouse"], "Barcode scanner setup",
            """
            Pair a new scanner with the base station, then scan the **Reset** and **Warehouse profile** codes from the manual.
            """,
            ["hardware"]),

        // Customer portal: a database without TNS, and a payment provider four stashes deep, its notes raised.
        Db(Admin, ["Customer portal", Prod], "Portal sessions database", "prod", "portal-db-prod.example.com", 5432, "portal_app", "Demo-Portal-Prod-6V",
            ["postgresql", "portal"]),
        Link(Admin, ["Customer portal", "Integrations", "Payments", Test], "Payment provider sandbox", "https://sandbox.payments.example.com", "documentation",
            ["payments", "sandbox"], notes: "The sandbox takes the test cards of the provider's documentation only.", levels: new() { ["notes"] = 2 }),
        Link(Admin, ["Customer portal", "Integrations", "Payments", Prod], "Payment provider dashboard", "https://dashboard.payments.example.com", "monitoring",
            ["payments", "monitoring"], notes: "The account of the dashboard is in the safe of the team lead.", levels: new() { ["notes"] = 3 }),
        Link(Editor, ["Customer portal"], "Portal status page", "https://status.example.com", "monitoring", ["monitoring", "portal"]),
        Note(Editor, ["Customer portal"], "Portal release checklist",
            """
            - [ ] Release notes are approved.
            - [ ] The test database is migrated.
            - [ ] The status page announces the window.
            """,
            ["release"]),

        // Handbook: what the whole team needs.
        Note(Admin, ["Handbook"], "On-call rota",
            """
            | Week | Primary | Secondary |
            |---|---|---|
            | 44 | Demo Admin | Demo Editor |
            | 45 | Demo Editor | Demo Reader |
            """,
            ["on-call"]),
        Note(Admin, ["Handbook"], "Welcome to the team",
            """
            Start with the *Team wiki*, then ask for your Kiler account and level.
            """,
            ["onboarding"], description: "Read this first."),
        Link(Admin, ["Handbook"], "Team wiki", "https://wiki.example.com/team", "documentation", ["docs", "onboarding"]),
        Link(Admin, ["Handbook"], "Central logs", "https://logs.example.com", "logs", ["logs"]),
        Link(Admin, ["Handbook"], "Holiday calendar", "https://calendar.example.com/team", "other", ["calendar"]),
        Note(Editor, ["Handbook"], "Database access requests",
            """
            Ask for access to a test database in the service desk, with the name of the record in Kiler.
            """,
            ["access"], description: "Who grants access to the databases of test.", levels: new() { ["description"] = 2 }),
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
    /// The made-up records of the volume mode, in groups of four of each template: a database, a query on it, a link and a note
    /// of one word, system and environment, so the search, the filters and the tree have many of each. The password of a
    /// database is at the level of its environment, and the note of each group rises from 1 to 3.
    /// </summary>
    private static IEnumerable<DemoRecord> Volume(int count)
    {
        string[] envs = ["prod", "test", "dev"];
        string[] envStashes = [Prod, Test, Dev];
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
                0 => Db(Admin, path, db, envs[env], $"{word.ToLowerInvariant()}-{group:0000}-{envs[env]}.example.com", env == 0 ? 1521 : 5432,
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

    /// <summary>A TNS entry of an Oracle database, made up.</summary>
    private static string Tns(string host, string service) =>
        $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={host})(PORT=1521))(CONNECT_DATA=(SERVICE_NAME={service})))";

    /// <summary>A record of the template <c>db</c> ("Server / database").</summary>
    private static DemoRecord Db(
        string author, string[] path, string name, string env, string host, int port, string user, string password, string[] tags,
        string? tns = null, string? description = null) =>
        new(author, "db", path, name, tags, Values(("env", env), ("host", host), ("port", FieldValue.OfNumber(port)), ("tns", tns), ("user", user), ("password", password)),
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
