using System.Text;
using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>
/// The admin seal in the files (M4.11): the <c>stamp</c> of a user file covers its rights, and that of <c>levels.json</c>,
/// <c>settings.json</c> and a template every member but itself, with the id and the version (<see cref="FileSeal"/>). A
/// file sealed by an admin key is sealed by its public key only; a changed member of the text, or a stamp that is not such
/// a seal, reads as not sealed.
/// </summary>
public sealed class SealTests : IDisposable
{
    private static readonly DocumentKey DemoUser = UserFile.KeyOf("demo.user");
    private static readonly DocumentKey DemoTemplate = TemplateFile.KeyOf("demo");

    private readonly AdminKey _admin = AdminKey.New();
    private readonly AdminKey _other = AdminKey.New();

    /// <summary>Stamps that are no seal of anything: not base64, empty, and base64 of 64 or 63 zero bytes.</summary>
    public static TheoryData<string> NotSeals => new() { "not a seal", "", Convert.ToBase64String(new byte[64]), Convert.ToBase64String(new byte[63]) };

    public void Dispose()
    {
        _admin.Dispose();
        _other.Dispose();
    }

    [Fact]
    public void AUserFile_Sealed_IsSealedByItsAdminKey_AfterTheDataLayer_AndNotByAnother()
    {
        using var data = new TestData();
        var user = User().Sealed(_admin);

        var read = ReadUser(data.WriteAndRead(user.Key, user.ToContent()));

        Assert.Equal(user.Stamp, read.Stamp);
        Assert.True(read.IsSealedBy(_admin.PublicKey));
        Assert.False(read.IsSealedBy(_other.PublicKey));
        Assert.Null(User().Stamp);
        Assert.False(User().IsSealedBy(_admin.PublicKey));
    }

    [Theory]
    [InlineData("displayName", "\"Other User\"")]
    [InlineData("level", "1")]
    [InlineData("level", "3")]
    [InlineData("canEdit", "false")]
    [InlineData("isAdmin", "true")]
    public void AUserFile_WithARightChanged_IsNotSealed(string member, string value)
    {
        var content = User().Sealed(_admin).ToContent();

        Change(content, member, value);

        Assert.False(ReadUser(DemoUser, content).IsSealedBy(_admin.PublicKey));
    }

    [Fact]
    public void AUserFile_UnderAnotherAccount_IsNotSealed()
    {
        var content = User().Sealed(_admin).ToContent();

        Assert.True(ReadUser(DemoUser, content).IsSealedBy(_admin.PublicKey));
        Assert.False(ReadUser(UserFile.KeyOf("demo.other"), content).IsSealedBy(_admin.PublicKey));
    }

    [Theory]
    [InlineData("salt", "\"other-salt\"")]
    [InlineData("keys", "{\"1\":\"other-1\"}")]
    [InlineData("mustChangePassword", "true")]
    [InlineData("adminPublicKey", "\"other-public\"")]
    [InlineData("adminPrivateKey", null)]
    public void AUserFile_WithItsKeysOrItsMarkChanged_StaysSealed(string member, string? value)
    {
        var content = User().Sealed(_admin).ToContent();

        Change(content, member, value);

        Assert.True(ReadUser(DemoUser, content).IsSealedBy(_admin.PublicKey));
    }

    [Fact]
    public void AUserFile_WithAMemberThatTheModelDoesNotKnow_StaysSealed_AsItsSealCoversOnlyTheRights()
    {
        var content = User().Sealed(_admin).ToContent();

        Add(content, "later", "{\"remember\":true}");

        Assert.True(ReadUser(DemoUser, content).IsSealedBy(_admin.PublicKey));
    }

    [Fact]
    public void AUserFile_WithNewKeys_KeepsItsSeal_AtTheNextVersion()
    {
        using var data = new TestData();
        var user = User().Sealed(_admin);
        data.WriteAndRead(user.Key, user.ToContent());

        var changed = user.WithKeys(new LockedKeys("new-salt", new Dictionary<int, string> { [1] = "new-1" }), mustChangePassword: true);
        var document = data.ReplaceAndRead(user.Key, 1, changed.ToContent());

        Assert.Equal(2, document.Version);
        var read = ReadUser(document);
        Assert.Equal(("new-salt", null, true), (read.Keys.Salt, read.Keys.AdminPublicKey, read.MustChangePassword));
        Assert.Equal(user.Stamp, read.Stamp);
        Assert.True(read.IsSealedBy(_admin.PublicKey));
    }

    [Fact]
    public void LevelsJson_SealedForTheVersionOfItsWrite_IsSealed_AfterTheDataLayer() =>
        AssertSealedThroughTheLayer(LevelsFile.Key, version => Levels().Sealed(_admin, version).ToContent(), document => ReadLevels(document).IsSealedBy);

    [Fact]
    public void SettingsJson_SealedForTheVersionOfItsWrite_IsSealed_AfterTheDataLayer() =>
        AssertSealedThroughTheLayer(SettingsFile.Key, version => Settings().Sealed(_admin, version).ToContent(), document => ReadSettings(document).IsSealedBy);

    [Fact]
    public void ATemplate_SealedForTheVersionOfItsWrite_IsSealed_AfterTheDataLayer() =>
        AssertSealedThroughTheLayer(DemoTemplate, version => Template().Sealed(_admin, version).ToContent(), document => ReadTemplate(document).IsSealedBy);

    [Fact]
    public void TheTeam_TakesLevelsJsonThatItWrote_AsReadAtTheVersionOfTheWrite()
    {
        // M4.11 security review: a seal made for another version fails in the Kiler that wrote it too, as in every other.
        using var data = new TestData();
        data.WriteAndRead(LevelsFile.Key, Levels().ToContent());
        var team = new Team(data.Copy);

        Assert.Equal(WriteOutcome.Written, team.Replace(Levels().Sealed(_admin, 2), 1).Outcome);
        Assert.True(team.Files.Levels?.Model?.IsSealedBy(_admin.PublicKey));

        Assert.Equal(WriteOutcome.Written, team.Replace(Levels().Sealed(_admin, 2), 2).Outcome);
        Assert.Equal(3, team.Files.Levels?.Version);
        Assert.False(team.Files.Levels?.Model?.IsSealedBy(_admin.PublicKey));
    }

    [Fact]
    public void Sealed_KeepsTheValues_AndWritesTheStampLast()
    {
        var levels = Levels();
        var settings = Settings();
        var template = Template();

        var sealedLevels = levels.Sealed(_admin, 1).ToContent();
        var sealedSettings = settings.Sealed(_admin, 1).ToContent();
        var sealedTemplate = template.Sealed(_admin, 1).ToContent();

        foreach (var (content, original) in new[] { (sealedLevels, levels.ToContent()), (sealedSettings, settings.ToContent()), (sealedTemplate, template.ToContent()) })
        {
            Assert.Equal(FileSeal.Member, content.Last().Key);
            content.Remove(FileSeal.Member);
            Assert.True(JsonNode.DeepEquals(original, content));
        }
    }

    [Theory]
    [InlineData("levels/1/name", "\"Other\"")]
    [InlineData("levels/0/name", "\"Visitor\"")]
    [InlineData("levels/2/check", "\"other-check\"")]
    [InlineData("levels/2/check", null)]
    [InlineData("recovery/salt", "\"other-salt\"")]
    [InlineData("recovery/keys/1", "\"other-1\"")]
    [InlineData("recovery/adminPublicKey", "\"other-public\"")]
    [InlineData("recovery/adminPrivateKey", null)]
    public void LevelsJson_WithAMemberChanged_IsNotSealed(string path, string? value)
    {
        var content = Levels().Sealed(_admin, 1).ToContent();

        Change(content, path, value);

        Assert.False(ReadLevels(1, content).IsSealedBy(_admin.PublicKey));
    }

    [Fact]
    public void LevelsJson_WithItsTopLevelCut_IsNotSealed()
    {
        var content = Levels().Sealed(_admin, 1).ToContent();

        Change(content, "levels/2", null);
        Change(content, "recovery/keys/2", null);

        Assert.Equal(1, ReadLevels(1, content).Top);
        Assert.False(ReadLevels(1, content).IsSealedBy(_admin.PublicKey));
    }

    [Theory]
    [InlineData("guestMode", "true")]
    [InlineData("autoLockMinutes", "1440")]
    public void SettingsJson_WithAMemberChanged_IsNotSealed(string path, string value)
    {
        var content = Settings().Sealed(_admin, 1).ToContent();

        Change(content, path, value);

        Assert.False(ReadSettings(1, content).IsSealedBy(_admin.PublicKey));
    }

    [Theory]
    [InlineData("name", "\"Other\"")]
    [InlineData("icon", "\"other\"")]
    [InlineData("fields/1/level", "2")]
    [InlineData("fields/1/required", null)]
    [InlineData("fields/0/options", "[\"prod\",\"test\",\"dev\"]")]
    [InlineData("fields/2/levelByEnv/prod", "2")]
    [InlineData("fields/1", null)]
    public void ATemplate_WithAMemberChanged_IsNotSealed(string path, string? value)
    {
        var content = Template().Sealed(_admin, 1).ToContent();

        Change(content, path, value);

        Assert.False(ReadTemplate(1, content).IsSealedBy(_admin.PublicKey));
    }

    [Fact]
    public void ATemplate_UnderAnotherId_OrAtAnotherVersion_IsNotSealed()
    {
        var content = Template().Sealed(_admin, 2).ToContent();

        Assert.True(ReadTemplate(DemoTemplate, 2, content).IsSealedBy(_admin.PublicKey));
        Assert.False(ReadTemplate(TemplateFile.KeyOf("other"), 2, content).IsSealedBy(_admin.PublicKey));
        Assert.False(ReadTemplate(DemoTemplate, 1, content).IsSealedBy(_admin.PublicKey));
        Assert.False(ReadTemplate(DemoTemplate, 3, content).IsSealedBy(_admin.PublicKey));
    }

    [Theory]
    [InlineData("later", "true")]
    [InlineData("levels/0/remember", "false")]
    [InlineData("recovery/later", "null")]
    public void LevelsJson_WithAMemberAdded_ThatTheModelDoesNotKnow_IsNotSealed(string path, string value)
    {
        var content = Levels().Sealed(_admin, 1).ToContent();

        Add(content, path, value);

        Assert.False(ReadLevels(1, content).IsSealedBy(_admin.PublicKey));
    }

    [Fact]
    public void SettingsJson_AndATemplate_WithAMemberAdded_ThatTheModelDoesNotKnow_AreNotSealed()
    {
        var settings = Settings().Sealed(_admin, 1).ToContent();
        var template = Template().Sealed(_admin, 1).ToContent();

        Add(settings, "later", "1");
        Add(template, "fields/1/later", "\"x\"");

        Assert.False(ReadSettings(1, settings).IsSealedBy(_admin.PublicKey));
        Assert.False(ReadTemplate(1, template).IsSealedBy(_admin.PublicKey));
    }

    [Fact]
    public void LevelsJson_WithItsMembersInAnotherOrder_IsNotSealed()
    {
        // The text keeps the order of the file (decided by the user in M4.11).
        var content = Levels().Sealed(_admin, 1).ToContent();
        var reordered = new JsonObject
        {
            ["recovery"] = content["recovery"]!.DeepClone(),
            ["levels"] = content["levels"]!.DeepClone(),
            [FileSeal.Member] = content[FileSeal.Member]!.DeepClone(),
        };

        Assert.True(ReadLevels(1, content).IsSealedBy(_admin.PublicKey));
        Assert.False(ReadLevels(1, reordered).IsSealedBy(_admin.PublicKey));
    }

    [Fact]
    public void AMemberThatTheModelDoesNotKnow_IsCovered_SoTheFileOfALaterKiler_IsSealedForAnOlderOne()
    {
        using var data = new TestData();
        var content = Levels().ToContent();
        Add(content, "later", "{\"remember\":true}");
        Add(content, "levels/1/autoLockMinutes", "5");
        content[FileSeal.Member] = FileSeal.Make(_admin, SealPurpose.Levels, LevelsFile.Key, 1, content).Stamp;

        var document = data.WriteAndRead(LevelsFile.Key, content);
        var levels = ReadLevels(document);

        Assert.True(levels.IsSealedBy(_admin.PublicKey));
        var changed = document.Content.DeepClone().AsObject();
        Change(changed, "later/remember", "false");
        Assert.False(ReadLevels(1, changed).IsSealedBy(_admin.PublicKey));
        var removed = document.Content.DeepClone().AsObject();
        Change(removed, "levels/1/autoLockMinutes", null);
        Assert.False(ReadLevels(1, removed).IsSealedBy(_admin.PublicKey));

        // An admin of this Kiler seals again only what it knows and writes.
        var resealed = data.ReplaceAndRead(LevelsFile.Key, 1, levels.Sealed(_admin, 2).ToContent());
        Assert.False(resealed.Content.ContainsKey("later"));
        Assert.True(ReadLevels(resealed).IsSealedBy(_admin.PublicKey));
    }

    [Fact]
    public void AFileWithoutAStamp_IsNotSealed()
    {
        var levels = ReadLevels(1, Levels().ToContent());
        var settings = ReadSettings(1, Settings().ToContent());
        var template = ReadTemplate(1, Template().ToContent());

        Assert.Equal((null, null, null), (levels.Stamp, settings.Stamp, template.Stamp));
        Assert.False(levels.IsSealedBy(_admin.PublicKey));
        Assert.False(settings.IsSealedBy(_admin.PublicKey));
        Assert.False(template.IsSealedBy(_admin.PublicKey));
    }

    [Theory]
    [MemberData(nameof(NotSeals))]
    public void AStampThatIsNotASeal_IsNotSealed(string stamp)
    {
        var user = User().ToContent();
        var levels = Levels().ToContent();
        var settings = Settings().ToContent();
        var template = Template().ToContent();
        foreach (var content in new[] { user, levels, settings, template })
        {
            content[FileSeal.Member] = stamp;
        }

        Assert.False(ReadUser(DemoUser, user).IsSealedBy(_admin.PublicKey));
        Assert.False(ReadLevels(1, levels).IsSealedBy(_admin.PublicKey));
        Assert.False(ReadSettings(1, settings).IsSealedBy(_admin.PublicKey));
        Assert.False(ReadTemplate(1, template).IsSealedBy(_admin.PublicKey));
    }

    [Fact]
    public void ASealOfTheSameText_ForAnotherKindOfFile_IsNotSealed()
    {
        var user = User().ToContent();
        var levels = Levels().ToContent();
        var settings = Settings().ToContent();
        var template = Template().ToContent();
        var userText = FileSeal.TextOf("demo.user", "Demo User", 2, true, false);
        var levelsText = FileSeal.TextOf(LevelsFile.Key, 1, levels);
        var settingsText = FileSeal.TextOf(SettingsFile.Key, 1, settings);
        var templateText = FileSeal.TextOf(DemoTemplate, 1, template);

        foreach (var purpose in Enum.GetValues<SealPurpose>())
        {
            user[FileSeal.Member] = _admin.Seal(purpose, userText);
            levels[FileSeal.Member] = _admin.Seal(purpose, levelsText);
            settings[FileSeal.Member] = _admin.Seal(purpose, settingsText);
            template[FileSeal.Member] = _admin.Seal(purpose, templateText);

            Assert.Equal(purpose == SealPurpose.UserFile, ReadUser(DemoUser, user).IsSealedBy(_admin.PublicKey));
            Assert.Equal(purpose == SealPurpose.Levels, ReadLevels(1, levels).IsSealedBy(_admin.PublicKey));
            Assert.Equal(purpose == SealPurpose.Settings, ReadSettings(1, settings).IsSealedBy(_admin.PublicKey));
            Assert.Equal(purpose == SealPurpose.Template, ReadTemplate(1, template).IsSealedBy(_admin.PublicKey));
        }
    }

    [Fact]
    public void TheTextOfAFileSealedWhole_IsCompactJson_OfTheIdAndVersion_ThenEachMemberButTheStampInItsOrder_EveryCharacterOutsideAsciiEscaped()
    {
        // Pinned: a version of .NET that escaped otherwise would break every seal (M3.md, "The accepted limits of the seal", 6).
        // The id and the version of the content, in any case, are left out, as the layer leaves them out of the file.
        var content = JsonNode.Parse("""{ "name": "Тест <&'\"+`>\\ 😀", "Id": "x", "VERSION": 5, "n": 1.50, "list": [true, null, { "b": 2, "a": 1 }], "stamp": "s" }""")!.AsObject();

        Assert.Equal(
            "{\"id\":\"demo\",\"version\":7,\"name\":\"" + Escaped("Тест") + " " + Escaped("<&'\"+`>") + "\\\\ " + Escaped("😀") + "\",\"n\":1.50,\"list\":[true,null,{\"b\":2,\"a\":1}]}",
            Encoding.UTF8.GetString(FileSeal.TextOf(DemoTemplate, 7, content)));
    }

    [Fact]
    public void TheTextOfAUserFile_IsCompactJson_OfItsRights()
    {
        Assert.Equal(
            "{\"id\":\"demo.user\",\"displayName\":\"" + Escaped("Демо") + " User\",\"level\":2,\"canEdit\":true,\"isAdmin\":false}",
            Encoding.UTF8.GetString(FileSeal.TextOf("demo.user", "Демо User", 2, true, false)));
    }

    [Fact]
    public void TheText_DoesNotHangOnTheSpacesOrTheEscapesOfTheFile()
    {
        var plain = JsonNode.Parse("""{"name":"Тест","list":[1,2]}""")!.AsObject();
        var spaced = JsonNode.Parse($$"""{ "{{Escaped("name")}}" : "{{Escaped("Тест").ToLowerInvariant()}}",{{"\n"}}  "list" : [ 1 , 2 ] }""")!.AsObject();

        Assert.Equal(FileSeal.TextOf(DemoTemplate, 1, plain), FileSeal.TextOf(DemoTemplate, 1, spaced));
    }

    /// <summary>
    /// Writes the content sealed for version 1 as a new document, and over it the content sealed for version 2, each sealed
    /// once read back; then the content sealed for version 2 again, as version 3, which is not.
    /// </summary>
    private void AssertSealedThroughTheLayer(DocumentKey key, Func<int, JsonObject> sealedFor, Func<Document, Func<string, bool>> isSealedBy)
    {
        using var data = new TestData();

        var first = isSealedBy(data.WriteAndRead(key, sealedFor(1)));
        Assert.True(first(_admin.PublicKey));
        Assert.False(first(_other.PublicKey));

        var second = data.ReplaceAndRead(key, 1, sealedFor(2));
        Assert.Equal(2, second.Version);
        Assert.True(isSealedBy(second)(_admin.PublicKey));

        var third = data.ReplaceAndRead(key, 2, sealedFor(2));
        Assert.Equal(3, third.Version);
        Assert.False(isSealedBy(third)(_admin.PublicKey));
    }

    /// <summary>
    /// Sets the member at <paramref name="path"/>, which is there, to the JSON of <paramref name="value"/>, or takes it out
    /// when it is null; the path names the members and the indexes of the arrays, split by <c>/</c>.
    /// </summary>
    private static void Change(JsonObject content, string path, string? value)
    {
        var (parent, last) = Parent(content, path);
        if (parent is JsonArray array)
        {
            var index = int.Parse(last, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(index, 0, array.Count - 1);
            if (value is null)
            {
                array.RemoveAt(index);
            }
            else
            {
                array[index] = JsonNode.Parse(value);
            }

            return;
        }

        var parentObject = parent.AsObject();
        Assert.True(parentObject.ContainsKey(last), path);
        if (value is null)
        {
            parentObject.Remove(last);
        }
        else
        {
            parentObject[last] = JsonNode.Parse(value);
        }
    }

    /// <summary>Adds a member at <paramref name="path"/>, which is not there yet, with the JSON of <paramref name="value"/>.</summary>
    private static void Add(JsonObject content, string path, string value)
    {
        var (parent, last) = Parent(content, path);
        var parentObject = parent.AsObject();
        Assert.False(parentObject.ContainsKey(last), path);
        parentObject[last] = JsonNode.Parse(value);
    }

    private static (JsonNode Parent, string Last) Parent(JsonObject content, string path)
    {
        var parts = path.Split('/');
        JsonNode parent = content;
        foreach (var part in parts[..^1])
        {
            parent = (parent is JsonArray array ? array[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)] : parent[part])
                ?? throw new InvalidOperationException($"{path} has no {part}.");
        }

        return (parent, parts[^1]);
    }

    private static string Escaped(string text) => string.Concat(text.Select(character => $"\\u{(int)character:X4}"));

    /// <summary>A user of level 2 who is an editor, with made-up keys and admin key, as the model does not unlock them.</summary>
    private static UserFile User() => new(
        "demo.user", "Demo User", level: 2, canEdit: true, isAdmin: false, mustChangePassword: false,
        new LockedKeys("salt", new Dictionary<int, string> { [1] = "locked-1", [2] = "locked-2" }, "public", "private"));

    /// <summary>The levels 0 to 2 with made-up checks, and a recovery set with made-up keys and admin key.</summary>
    private static LevelsFile Levels() => new(
        [new(0, "Guest", null), new(1, "Team", "check-1"), new(2, "Тест", "check-2")],
        new LockedKeys("salt", new Dictionary<int, string> { [1] = "locked-1", [2] = "locked-2" }, "public", "private"));

    private static SettingsFile Settings() => new(guestMode: false, autoLockMinutes: 30);

    private static TemplateFile Template() => new("demo", "Demo", "database",
    [
        new("env", "Environment", FieldType.Select, level: 0, options: ["prod", "test"]),
        new("host", "Host", FieldType.Text, level: 1, required: true),
        new("password", "Password", FieldType.Secret, level: 1, levelByEnv: new Dictionary<string, int> { ["prod"] = 3, ["test"] = 2 }),
    ]);

    private static UserFile ReadUser(Document document)
    {
        Assert.True(UserFile.TryRead(document, out var user, out var invalid), invalid);
        return user;
    }

    private static UserFile ReadUser(DocumentKey key, JsonObject content)
    {
        Assert.True(UserFile.TryRead(key, content, out var user, out var invalid), invalid);
        return user;
    }

    private static LevelsFile ReadLevels(Document document)
    {
        Assert.True(LevelsFile.TryRead(document, out var levels, out var invalid), invalid);
        return levels;
    }

    private static LevelsFile ReadLevels(int version, JsonObject content)
    {
        Assert.True(LevelsFile.TryRead(LevelsFile.Key, version, content, out var levels, out var invalid), invalid);
        return levels;
    }

    private static SettingsFile ReadSettings(Document document)
    {
        Assert.True(SettingsFile.TryRead(document, out var settings, out var invalid), invalid);
        return settings;
    }

    private static SettingsFile ReadSettings(int version, JsonObject content)
    {
        Assert.True(SettingsFile.TryRead(SettingsFile.Key, version, content, out var settings, out var invalid), invalid);
        return settings;
    }

    private static TemplateFile ReadTemplate(Document document)
    {
        Assert.True(TemplateFile.TryRead(document, out var template, out var invalid), invalid);
        return template;
    }

    private static TemplateFile ReadTemplate(int version, JsonObject content) => ReadTemplate(DemoTemplate, version, content);

    private static TemplateFile ReadTemplate(DocumentKey key, int version, JsonObject content)
    {
        Assert.True(TemplateFile.TryRead(key, version, content, out var template, out var invalid), invalid);
        return template;
    }
}
