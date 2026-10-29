using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core.Tests;

public class LevelsFileTests
{
    // Made-up values: the model does not decrypt the checks or the keys.
    private const string Recovery = "\"recovery\":{\"salt\":\"salt\",\"keys\":{\"1\":\"locked-1\",\"2\":\"locked-2\"}}";

    [Fact]
    public void LevelsFile_ReadsWhatItWrites_ThroughTheDataLayer()
    {
        using var data = new TestData();
        var levels = new LevelsFile(
            [new(0, "Guest", null), new(1, "Team", "check-1"), new(2, "Тест", "check-2"), new(3, "Prod", "check-3")],
            new LockedKeys("salt", new Dictionary<int, string> { [3] = "locked-3", [1] = "locked-1", [2] = "locked-2" }));

        var document = data.WriteAndRead(LevelsFile.Key, levels.ToContent());

        Assert.True(LevelsFile.TryRead(document, out var read, out var invalid), invalid);
        Assert.Equal(levels.Levels, read.Levels);
        Assert.Equal(3, read.Top);
        Assert.Equal("salt", read.Recovery.Salt);
        Assert.Equal(levels.Recovery.Keys, read.Recovery.Keys);
        Assert.True(JsonNode.DeepEquals(levels.ToContent(), read.ToContent()));
    }

    [Fact]
    public void ToContent_WritesTheLevelsInOrder_TheChecksFromLevel1_AndTheRecoveryKeysByLevel()
    {
        var levels = new LevelsFile(
            [new(0, "Guest", null), new(1, "Team", "check-1"), new(2, "Prod", "check-2")],
            new LockedKeys("salt", new Dictionary<int, string> { [2] = "locked-2", [1] = "locked-1" }));

        Assert.Equal(
            $$"""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":"check-1"},{"level":2,"name":"Prod","check":"check-2"}],{{Recovery}}}""",
            levels.ToContent().ToJsonString());
    }

    [Fact]
    public void AMissingCheck_IsNull_AndTheFileIsValid()
    {
        // It counts as not verified at the sign-in (M4.4), so only its level is locked.
        var levels = Read("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team"},{"level":2,"name":"Prod","check":"check-2"}],""" + Recovery + "}");

        Assert.Null(levels.Levels[1].Check);
        Assert.Equal("check-2", levels.Levels[2].Check);
    }

    [Fact]
    public void AMemberThatTheModelDoesNotKnow_IsSkipped()
    {
        var levels = Read("""{"levels":[{"level":0,"name":"Guest","remember":true},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],"stamp":"s",""" + Recovery + "}");

        Assert.Equal("Guest", levels.Levels[0].Name);
        Assert.False(levels.ToContent().ContainsKey("stamp"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(LevelsFile.MaxTop)]
    public void TheTopLevel_From1To9_IsValid(int top)
    {
        var levels = Read(Content(top));

        Assert.Equal(top, levels.Top);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(LevelsFile.MaxTop + 1)]
    public void TheTopLevel_Not1To9_IsInvalid(int top)
    {
        Assert.Contains("not 0 to a top level from 1 to 9", Invalid(Content(top)), StringComparison.Ordinal);
    }

    [Fact]
    public void AName_Of40Characters_IsValid_AndOf41_IsInvalid()
    {
        Assert.Equal(new string('a', 40), Read(Levels($$"""{"level":0,"name":"{{new string('a', 40)}}"}""", """{"level":1,"name":"Team","check":"c"}""", """{"level":2,"name":"Prod","check":"c"}""")).Levels[0].Name);
        Assert.Contains("name of level 0 is blank or longer than 40", Invalid(Levels($$"""{"level":0,"name":"{{new string('a', 41)}}"}""", """{"level":1,"name":"Team","check":"c"}""", """{"level":2,"name":"Prod","check":"c"}""")), StringComparison.Ordinal);
    }

    [Theory]
    // Not the types of the model.
    [InlineData("""{"levels":{},""" + Recovery + "}", "type")]
    [InlineData("""{"levels":[{"level":"0","name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],""" + Recovery + "}", "type")]
    [InlineData("""{"levels":[{"level":0.5,"name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],""" + Recovery + "}", "type")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":5},{"level":2,"name":"Prod","check":"c"}],""" + Recovery + "}", "type")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],"recovery":[]}""", "type")]
    // A missing member, also one in another case.
    [InlineData("{" + Recovery + "}", "levels is missing")]
    [InlineData("""{"levels":null,""" + Recovery + "}", "levels is missing")]
    [InlineData("""{"Levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],""" + Recovery + "}", "levels is missing")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},null,{"level":2,"name":"Prod","check":"c"}],""" + Recovery + "}", "Entry 1 of levels is null")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],""" + Recovery + "}", "Entry 1 of levels has no level")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"Level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],""" + Recovery + "}", "Entry 1 of levels has no level")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"check":"c"},{"level":2,"name":"Prod","check":"c"}],""" + Recovery + "}", "Entry 1 of levels has no name")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"Name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],""" + Recovery + "}", "Entry 1 of levels has no name")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}]}""", "recovery is missing")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],"Recovery":{"salt":"salt","keys":{}}}""", "recovery is missing")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],"recovery":{"keys":{}}}""", "recovery.salt is missing")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],"recovery":{"salt":"salt"}}""", "recovery.keys is missing")]
    // The levels out of order, twice or with a gap.
    [InlineData("""{"levels":[{"level":1,"name":"Team","check":"c"},{"level":0,"name":"Guest"},{"level":2,"name":"Prod","check":"c"}],""" + Recovery + "}", "each once and in order")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":1,"name":"Prod","check":"c"}],""" + Recovery + "}", "each once and in order")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":2,"name":"Team","check":"c"},{"level":3,"name":"Prod","check":"c"}],""" + Recovery + "}", "each once and in order")]
    [InlineData("""{"levels":[{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],""" + Recovery + "}", "each once and in order")]
    // The names.
    [InlineData("""{"levels":[{"level":0,"name":""},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],""" + Recovery + "}", "name of level 0 is blank")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"   ","check":"c"},{"level":2,"name":"Prod","check":"c"}],""" + Recovery + "}", "name of level 1 is blank")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"TEAM","check":"c"}],""" + Recovery + "}", "Level 2 has the name of a level below it")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Екип","check":"c"},{"level":2,"name":"еКИП","check":"c"}],""" + Recovery + "}", "Level 2 has the name of a level below it")]
    // Level 0 has no key.
    [InlineData("""{"levels":[{"level":0,"name":"Guest","check":"c"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],""" + Recovery + "}", "Level 0 has a check")]
    // The names of the recovery keys are the digits of the levels 1 to the top, and each has a value.
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],"recovery":{"salt":"salt","keys":{"x":"k"}}}""", "recovery.keys has a name that is not the digit of a level from 1 to 2")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],"recovery":{"salt":"salt","keys":{"0":"k"}}}""", "recovery.keys has a name that is not the digit")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],"recovery":{"salt":"salt","keys":{"01":"k"}}}""", "recovery.keys has a name that is not the digit")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],"recovery":{"salt":"salt","keys":{"3":"k"}}}""", "recovery.keys has a name that is not the digit")]
    [InlineData("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team","check":"c"},{"level":2,"name":"Prod","check":"c"}],"recovery":{"salt":"salt","keys":{"1":null}}}""", "recovery.keys has no value for level 1")]
    public void AContentThatDoesNotFit_IsInvalid_WithAReason(string json, string reason)
    {
        Assert.Contains(reason, Invalid(json), StringComparison.Ordinal);
    }

    [Fact]
    public void TheConstructor_RefusesWhatDoesNotFit()
    {
        var recovery = new LockedKeys("salt", new Dictionary<int, string> { [1] = "locked-1" });

        Assert.Throws<ArgumentException>(() => new LevelsFile([new(0, "Guest", null)], recovery));
        Assert.Throws<ArgumentException>(() => new LevelsFile([new(0, "Guest", null), new(1, "guest", "c")], recovery));
        Assert.Throws<ArgumentException>(() => new LevelsFile([new(0, "Guest", null), new(2, "Team", "c")], recovery));
        Assert.Throws<ArgumentException>(() => new LevelsFile([new(0, "Guest", "c"), new(1, "Team", "c")], recovery));
        Assert.Throws<ArgumentException>(() => new LevelsFile(
            [new(0, "Guest", null), new(1, "Team", "c")],
            new LockedKeys("salt", new Dictionary<int, string> { [2] = "locked-2" })));
    }

    [Fact]
    public void TheModel_DoesNotChangeWithTheKeysItWasGiven()
    {
        var keys = new Dictionary<int, string> { [1] = "locked-1" };
        var levels = new LevelsFile([new(0, "Guest", null), new(1, "Team", "c")], new LockedKeys("salt", keys));

        keys[1] = "changed";

        Assert.Equal("locked-1", levels.Recovery.Keys[1]);
    }

    [Fact]
    public void AnotherDocument_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => LevelsFile.TryRead(SettingsFile.Key, JsonNode.Parse(Content(2))!.AsObject(), out _, out _));
        Assert.Throws<ArgumentException>(() => LevelsFile.TryRead(DocumentKey.InFolder("records", "levels"), JsonNode.Parse(Content(2))!.AsObject(), out _, out _));
    }

    /// <summary>The levels 0 to <paramref name="top"/>, each with its recovery key.</summary>
    private static string Content(int top)
    {
        var levels = new JsonArray { new JsonObject { ["level"] = 0, ["name"] = "Guest" } };
        var keys = new JsonObject();
        for (var level = 1; level <= top; level++)
        {
            levels.Add(new JsonObject { ["level"] = level, ["name"] = $"Level {level}", ["check"] = "c" });
            keys[$"{level}"] = "k";
        }

        return new JsonObject { ["levels"] = levels, ["recovery"] = new JsonObject { ["salt"] = "salt", ["keys"] = keys } }.ToJsonString();
    }

    private static string Levels(params string[] levels) => $$"""{"levels":[{{string.Join(",", levels)}}],{{Recovery}}}""";

    private static LevelsFile Read(string json)
    {
        Assert.True(LevelsFile.TryRead(LevelsFile.Key, JsonNode.Parse(json)!.AsObject(), out var levels, out var invalid), invalid);
        return levels;
    }

    private static string Invalid(string json)
    {
        Assert.False(LevelsFile.TryRead(LevelsFile.Key, JsonNode.Parse(json)!.AsObject(), out var levels, out var invalid));
        Assert.Null(levels);
        return invalid;
    }
}
