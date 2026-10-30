using System.Text.Json.Nodes;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>The template model and its check (M5.2, design 6.2 to 6.5): each rule with a template that fits it and one that does not.</summary>
public class TemplateFileTests
{
    private const string Env = """{"key":"env","label":"Environment","type":"select","options":["prod","test","dev"],"level":0}""";

    private static readonly DocumentKey DemoKey = TemplateFile.KeyOf("demo");

    /// <summary>Fields that fit, each beside the template's other fields.</summary>
    public static TheoryData<string> FieldsThatFit => new()
    {
        """{"key":"host","label":"Host","type":"text","level":1}""",
        """{"key":"aB9","label":"Short","type":"multiline","level":0}""",
        $$"""{"key":"{{new string('k', TemplateField.MaxKeyLength)}}","label":"Long key","type":"markdown","level":9}""",
        """{"key":"guide","label":"Guide","type":"doc","level":0,"required":true}""",
        """{"key":"port","label":"Port","type":"number","level":1,"filter":true,"list":true}""",
        """{"key":"since","label":"Since","type":"date","level":0}""",
        """{"key":"url","label":"Address","type":"url","level":0}""",
        """{"key":"kind","label":"Kind","type":"select","options":["Docs","Logs"],"level":0,"filter":true}""",
        """{"key":"tns","label":"TNS","type":"code","language":"text","level":1}""",
        """{"key":"query","label":"Query","type":"code","language":"sql","dialect":"plsql","level":1}""",
        """{"key":"script","label":"Script","type":"code","languages":["bash","powershell"],"level":1}""",
        """{"key":"db","label":"Runs in","type":"ref","refTemplate":"db","level":1}""",
        """{"key":"password","label":"Password","type":"secret","level":1,"required":true}""",
        """{"key":"password","label":"Password","type":"secret","levelByEnv":{"prod":3,"test":2,"dev":1}}""",
        """{"key":"user","label":"User","type":"text","level":1,"levelByEnv":{"prod":3}}""",
        """{"key":"notes","label":"Notes","type":"markdown","level":1,"unknown":"skipped"}""",
    };

    /// <summary>Contents that do not fit, with the reason; the fields are put after <see cref="Env"/> by <see cref="Template"/> unless the content is whole.</summary>
    public static TheoryData<string, string> ContentsThatDoNotFit => new()
    {
        { """{"icon":"demo","fields":[]}""", "name is missing." },
        { """{"name":" ","icon":"demo","fields":[]}""", "name is blank or longer than 100 characters." },
        { $$"""{"name":"{{new string('n', TemplateFile.MaxNameLength + 1)}}","icon":"demo","fields":[]}""", "name is blank or longer than 100 characters." },
        { """{"name":"Demo","fields":[]}""", "icon is missing." },
        { """{"name":"Demo","icon":"","fields":[]}""", "icon is blank or longer than 40 characters." },
        { $$"""{"name":"Demo","icon":"{{new string('i', TemplateFile.MaxIconLength + 1)}}","fields":[]}""", "icon is blank or longer than 40 characters." },
        { """{"name":"Demo","icon":"demo"}""", "fields is missing." },
        { """{"name":"Demo","icon":"demo","fields":[]}""", "The template has no fields." },
        { """{"name":"Demo","icon":"demo","fields":[null]}""", "Entry 0 of fields is null." },
        { """{"name":"Demo","icon":"demo","fields":{"host":{}}}""", ModelJson.WrongType },
        { """{"name":"Demo","icon":"demo","Fields":[]}""", "fields is missing." },
        { Template("""{"label":"Host","type":"text","level":1}"""), "Entry 1 of fields has no key." },
        { Template("""{"key":"host","type":"text","level":1}"""), "Entry 1 of fields has no label." },
        { Template("""{"key":"host","label":"Host","level":1}"""), "Entry 1 of fields: type is missing or is not one of text, multiline, markdown, doc, select, number, date, url, code, ref, secret." },
        { Template("""{"key":"host","label":"Host","type":"Text","level":1}"""), "Entry 1 of fields: type is missing or is not one of" },
        { Template("""{"key":"host","label":"Host","type":"text","level":"1"}"""), ModelJson.WrongType },
        { Template("""{"key":"1host","label":"Host","type":"text","level":1}"""), "Entry 1 of fields: key is not 1 to 40 ASCII letters and digits that start with a lowercase letter." },
        { Template("""{"key":"Host","label":"Host","type":"text","level":1}"""), "key is not 1 to 40" },
        { Template("""{"key":"db-host","label":"Host","type":"text","level":1}"""), "key is not 1 to 40" },
        { Template("""{"key":"","label":"Host","type":"text","level":1}"""), "key is not 1 to 40" },
        { Template($$"""{"key":"{{new string('k', TemplateField.MaxKeyLength + 1)}}","label":"Host","type":"text","level":1}"""), "key is not 1 to 40" },
        { Template("""{"key":"name","label":"Name","type":"text","level":0}"""), "Entry 1 of fields: key is a member of the header of a record (stash, name, description, tags, createdAt, createdBy, updatedAt, updatedBy, private), without regard to case." },
        { Template("""{"key":"tAGS","label":"Tags","type":"text","level":0}"""), "key is a member of the header" },
        { Template("""{"key":"updatedby","label":"By","type":"text","level":0}"""), "key is a member of the header" },
        { Template("""{"key":"host","label":"Host","type":"text","level":1}""", """{"key":"hOST","label":"Host","type":"text","level":1}"""), "Entry 2 of fields has the key of an entry above it, without regard to case." },
        { Template("""{"key":"host","label":" ","type":"text","level":1}"""), "Entry 1 of fields: label is blank or longer than 100 characters." },
        { Template($$"""{"key":"host","label":"{{new string('l', TemplateField.MaxLabelLength + 1)}}","type":"text","level":1}"""), "label is blank or longer" },
        { Template("""{"key":"host","label":"Host","type":"text"}"""), "Entry 1 of fields: The field has neither level nor levelByEnv." },
        { Template("""{"key":"host","label":"Host","type":"text","level":-1}"""), "Entry 1 of fields: A level of the field is not from 0 to 9." },
        { Template("""{"key":"host","label":"Host","type":"text","level":10}"""), "A level of the field is not from 0 to 9." },
        { Template("""{"key":"host","label":"Host","type":"text","levelByEnv":{"prod":10}}"""), "A level of the field is not from 0 to 9." },
        { Template("""{"key":"host","label":"Host","type":"text","levelByEnv":{}}"""), "Entry 1 of fields: levelByEnv is empty." },
        { Template("""{"key":"host","label":"Host","type":"text","levelByEnv":{"prod":null}}"""), "Entry 1 of fields: an entry of levelByEnv, options or languages is null." },
        { Template("""{"key":"password","label":"Password","type":"secret","level":0}"""), "Entry 1 of fields: A secret is below level 1." },
        { Template("""{"key":"password","label":"Password","type":"secret","levelByEnv":{"prod":3,"dev":0}}"""), "A secret is below level 1." },
        { Template("""{"key":"password","label":"Password","type":"secret","level":1,"list":true}"""), "Entry 1 of fields: A secret has list or filter, though it is never in the list or the search." },
        { Template("""{"key":"password","label":"Password","type":"secret","level":1,"filter":true}"""), "A secret has list or filter" },
        { Template("""{"key":"kind","label":"Kind","type":"select","level":0}"""), "Entry 1 of fields: options is missing or empty." },
        { Template("""{"key":"kind","label":"Kind","type":"select","options":[],"level":0}"""), "options is missing or empty." },
        { Template("""{"key":"kind","label":"Kind","type":"select","options":["Docs"," "],"level":0}"""), "Entry 1 of fields: An entry of options is blank or longer than 100 characters." },
        { Template("""{"key":"kind","label":"Kind","type":"select","options":["Docs",null],"level":0}"""), "an entry of levelByEnv, options or languages is null." },
        { Template("""{"key":"kind","label":"Kind","type":"select","options":["Docs","DOCS"],"level":0}"""), "Entry 1 of fields: An entry of options is there twice, without regard to case." },
        { Template("""{"key":"host","label":"Host","type":"text","options":["a"],"level":1}"""), "Entry 1 of fields: options is only for a select." },
        { Template("""{"key":"tns","label":"TNS","type":"code","level":1}"""), "Entry 1 of fields: A code field has either language or languages, not both or neither." },
        { Template("""{"key":"tns","label":"TNS","type":"code","language":"sql","languages":["sql"],"level":1}"""), "A code field has either language or languages, not both or neither." },
        { Template("""{"key":"tns","label":"TNS","type":"code","language":" ","level":1}"""), "Entry 1 of fields: language is blank or longer than 40 characters." },
        { Template("""{"key":"tns","label":"TNS","type":"code","languages":["sql"],"dialect":"plsql","level":1}"""), "Entry 1 of fields: dialect is not beside language, or is blank or longer than 40 characters." },
        { Template("""{"key":"tns","label":"TNS","type":"code","language":"sql","dialect":"","level":1}"""), "dialect is not beside language" },
        { Template("""{"key":"tns","label":"TNS","type":"code","languages":[],"level":1}"""), "Entry 1 of fields: languages is missing or empty." },
        { Template("""{"key":"tns","label":"TNS","type":"code","languages":["sql","SQL"],"level":1}"""), "An entry of languages is there twice, without regard to case." },
        { Template("""{"key":"host","label":"Host","type":"text","language":"sql","level":1}"""), "Entry 1 of fields: language, dialect and languages are only for code." },
        { Template("""{"key":"host","label":"Host","type":"text","dialect":"plsql","level":1}"""), "language, dialect and languages are only for code." },
        { Template("""{"key":"db","label":"Runs in","type":"ref","level":1}"""), "Entry 1 of fields: refTemplate is missing or is not the id of a template." },
        { Template("""{"key":"db","label":"Runs in","type":"ref","refTemplate":"Db/x","level":1}"""), "refTemplate is missing or is not the id of a template." },
        { Template("""{"key":"host","label":"Host","type":"text","refTemplate":"db","level":1}"""), "Entry 1 of fields: refTemplate is only for a ref." },
        { """{"name":"Demo","icon":"demo","fields":[{"key":"env","label":"Environment","type":"select","options":["prod"],"level":1}]}""", "Entry 0 of fields: The field env is not at level 0 alone, though it stays there." },
        { """{"name":"Demo","icon":"demo","fields":[{"key":"env","label":"Environment","type":"select","options":["prod"],"level":0,"levelByEnv":{"prod":0}}]}""", "The field env is not at level 0 alone" },
        { """{"name":"Demo","icon":"demo","fields":[{"key":"password","label":"Password","type":"secret","levelByEnv":{"prod":3}}]}""", "Entry 0 of fields has levelByEnv, but the template has no field env of type select." },
        { """{"name":"Demo","icon":"demo","fields":[{"key":"env","label":"Environment","type":"text","level":0},{"key":"password","label":"Password","type":"secret","levelByEnv":{"prod":3}}]}""", "Entry 1 of fields has levelByEnv, but the template has no field env of type select." },
        { Template("""{"key":"password","label":"Password","type":"secret","levelByEnv":{"prod":3,"staging":2}}"""), "Entry 1 of fields has a key in levelByEnv that is not an option of env." },
        { Template("""{"key":"password","label":"Password","type":"secret","levelByEnv":{"PROD":3}}"""), "Entry 1 of fields has a key in levelByEnv that is not an option of env." },
    };

    [Fact]
    public void TemplateFile_ReadsWhatItWrites_ThroughTheDataLayer_WithAFieldOfEachType()
    {
        using var data = new TestData();
        var template = new TemplateFile("demo", "Demo template", "database",
        [
            new("env", "Environment", FieldType.Select, level: 0, filter: true, options: ["prod", "test"]),
            new("host", "Host", FieldType.Text, level: 1, list: true, required: true),
            new("notes", "Notes", FieldType.Multiline, level: 1),
            new("guide", "Guide", FieldType.Markdown, level: 0),
            new("doc", "Document", FieldType.Doc, level: 0),
            new("port", "Port", FieldType.Number, level: 1),
            new("since", "Since", FieldType.Date, level: 0),
            new("url", "Address", FieldType.Url, level: 0),
            new("query", "Query", FieldType.Code, level: 1, language: "sql", dialect: "plsql"),
            new("script", "Script", FieldType.Code, level: 1, languages: ["bash", "python"]),
            new("db", "Runs in", FieldType.Ref, level: 1, refTemplate: "db"),
            new("password", "Password", FieldType.Secret, level: 1, levelByEnv: new Dictionary<string, int> { ["prod"] = 3, ["test"] = 2 }),
        ]);

        var document = data.WriteAndRead(template.Key, template.ToContent());

        Assert.True(TemplateFile.TryRead(document, out var read, out var invalid), invalid);
        Assert.Equal(("demo", "Demo template", "database"), (read.Id, read.Name, read.Icon));
        Assert.Equal(Enum.GetValues<FieldType>().Order(), read.Fields.Select(field => field.Type).Distinct().Order());
        var password = read.Fields[^1];
        Assert.Equal((1, 3, 2), (password.Level, password.LevelByEnv?["prod"], password.LevelByEnv?["test"]));
        Assert.Equal(template.ToContent().ToJsonString(), read.ToContent().ToJsonString());
        Assert.Equal(
            """{"key":"query","label":"Query","type":"code","language":"sql","dialect":"plsql","level":1}""",
            read.ToContent()["fields"]![8]!.ToJsonString());
    }

    [Theory]
    [MemberData(nameof(FieldsThatFit))]
    public void AFieldThatFitsItsRules_IsValid(string field)
    {
        Assert.True(TemplateFile.TryRead(DemoKey, Content(Template(field)), out var template, out var invalid), invalid);
        Assert.Equal(2, template.Fields.Count);
    }

    [Theory]
    [MemberData(nameof(ContentsThatDoNotFit))]
    public void AContentThatDoesNotFit_IsInvalid_WithAReason(string json, string reason)
    {
        Assert.False(TemplateFile.TryRead(DemoKey, Content(json), out var template, out var invalid));
        Assert.Null(template);
        Assert.Contains(reason, invalid, StringComparison.Ordinal);
    }

    [Fact]
    public void AReason_NeverRepeatsAValueOfTheShare()
    {
        Assert.False(TemplateFile.TryRead(DemoKey, Content(Template("""{"key":"secretValue1","label":"made-up-value","type":"text"}""")), out _, out var invalid));
        Assert.DoesNotContain("secretValue1", invalid, StringComparison.Ordinal);
        Assert.DoesNotContain("made-up-value", invalid, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConstructors_RefuseWhatDoesNotFit()
    {
        Assert.Throws<ArgumentException>(() => new TemplateField("password", "Password", FieldType.Secret, level: 0));
        Assert.Throws<ArgumentException>(() => new TemplateField("host", "Host", (FieldType)99, level: 1));
        Assert.Throws<ArgumentException>(() => new TemplateFile("demo", "Demo", "demo", []));
        Assert.Throws<ArgumentException>(() => new TemplateFile("Demo", "Demo", "demo", [new("host", "Host", FieldType.Text, level: 1)]));
    }

    [Fact]
    public void AnotherDocument_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => TemplateFile.TryRead(UserFile.KeyOf("demo"), Content(Template()), out _, out _));
        Assert.Throws<ArgumentException>(() => TemplateFile.TryRead(TemplateFile.KeyOf("demo").InPrivate("demo.user"), Content(Template()), out _, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(LevelsFile.MaxTop)]
    public void TheInitialTemplates_Fit_WithNoLevelAboveTheTopLevel(int top)
    {
        var templates = InitialTemplates.For(top);

        Assert.Equal(["db", "sql", "link", "note"], templates.Select(template => template.Id));
        Assert.Equal(["Server / database", "SQL query", "Link", "Note"], templates.Select(template => template.Name));
        foreach (var template in templates)
        {
            Assert.True(TemplateFile.TryRead(template.Key, template.ToContent(), out _, out var invalid), invalid);
            Assert.All(template.Fields, field => Assert.True(field.Level <= top || field.LevelByEnv?.Values.All(level => level <= top) == true, field.Key));
        }

        var password = Assert.Single(templates[0].Fields, field => field.Key == "password");
        Assert.Equal(FieldType.Secret, password.Type);
        Assert.Equal((Math.Min(3, top), Math.Min(2, top), 1), (password.LevelByEnv?["prod"], password.LevelByEnv?["test"], password.LevelByEnv?["dev"]));
        Assert.Equal("db", Assert.Single(templates[1].Fields, field => field.Type == FieldType.Ref).RefTemplate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(LevelsFile.MaxTop + 1)]
    public void TheInitialTemplates_NeedATopLevelFrom1To9(int top)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InitialTemplates.For(top));
    }

    /// <summary>A template with the field <c>env</c> of the design and <paramref name="fields"/> after it, as JSON.</summary>
    private static string Template(params string[] fields) =>
        $$"""{"name":"Demo","icon":"demo","fields":[{{string.Join(",", [Env, .. fields])}}]}""";

    private static JsonObject Content(string json) => JsonNode.Parse(json)!.AsObject();
}
