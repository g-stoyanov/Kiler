namespace Kiler.Core;

/// <summary>
/// The four initial templates (design 6.1 to 6.3, decided at the start of M5): "Server / database", "SQL query", "Link" and
/// "Note", in English. The wizard writes them into a new Kiler (<see cref="NewKiler"/>), so the demo data gets them by the
/// same path, and admins change them after the exam. Level 0 holds only what anyone with access to the share may read
/// (design 7.1): the environment, the links and their kind; addresses, queries and notes are at 1 or higher.
/// </summary>
public static class InitialTemplates
{
    /// <summary>
    /// The templates of a Kiler whose top level is <paramref name="top"/>: a level of the design above it is the top level,
    /// so a Kiler of fewer levels can write every field.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="top"/> is not from 1 to <see cref="LevelsFile.MaxTop"/>.</exception>
    public static IReadOnlyList<TemplateFile> For(int top)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(top, LevelsFile.MaxTop);
        int At(int level) => Math.Min(level, top);

        // Design 6.2: the user and the password of prod at 3, of test at 2 and of dev at 1.
        var byEnv = new Dictionary<string, int> { ["prod"] = At(3), ["test"] = At(2), ["dev"] = At(1) };
        return
        [
            new("db", "Server / database", "database",
            [
                new("env", "Environment", FieldType.Select, level: 0, filter: true, options: ["prod", "test", "dev"]),
                new("host", "Host", FieldType.Text, level: 1, list: true),
                new("port", "Port", FieldType.Number, level: 1),
                new("tns", "TNS", FieldType.Code, level: 1, language: "text"),
                new("user", "User", FieldType.Text, levelByEnv: byEnv),
                new("password", "Password", FieldType.Secret, levelByEnv: byEnv),
            ]),
            new("sql", "SQL query", "code",
            [
                new("db", "Runs in", FieldType.Ref, level: 1, refTemplate: "db"),
                new("query", "Query", FieldType.Code, level: 1, language: "sql", dialect: "plsql"),
                new("notes", "Notes", FieldType.Markdown, level: 1),
            ]),
            new("link", "Link", "link",
            [
                new("url", "Address", FieldType.Url, level: 0, required: true, list: true),
                new("kind", "Kind", FieldType.Select, level: 0, filter: true, options: ["documentation", "logs", "monitoring", "other"]),
                new("notes", "Notes", FieldType.Markdown, level: 1),
            ]),
            new("note", "Note", "note",
            [
                new("text", "Text", FieldType.Markdown, level: 1),
            ]),
        ];
    }
}
