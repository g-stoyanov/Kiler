using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core.Tests;

public class UserFileTests
{
    private static readonly DocumentKey DemoUser = UserFile.KeyOf("demo.user");

    [Fact]
    public void UserFile_ReadsWhatItWrites_ThroughTheDataLayer()
    {
        using var data = new TestData();
        var user = new UserFile(
            "demo.user", "Демо Потребител", level: 2, canEdit: true, isAdmin: false, mustChangePassword: true,
            new LockedKeys("salt", new Dictionary<int, string> { [2] = "locked-2", [1] = "locked-1" }));

        var document = data.WriteAndRead(user.Key, user.ToContent());

        Assert.True(UserFile.TryRead(document, out var read, out var invalid), invalid);
        Assert.Equal("demo.user", read.Account);
        Assert.Equal("Демо Потребител", read.DisplayName);
        Assert.Equal(2, read.Level);
        Assert.True(read.CanEdit);
        Assert.False(read.IsAdmin);
        Assert.True(read.MustChangePassword);
        Assert.Equal("salt", read.Keys.Salt);
        Assert.Equal(user.Keys.Keys, read.Keys.Keys);
        Assert.True(JsonNode.DeepEquals(user.ToContent(), read.ToContent()));
    }

    [Fact]
    public void ToContent_WritesTheFieldsOfTheDecisions_AndTheKeysByLevel()
    {
        var user = new UserFile(
            "demo.user", "Demo User", level: 2, canEdit: false, isAdmin: true, mustChangePassword: false,
            new LockedKeys("salt", new Dictionary<int, string> { [2] = "locked-2", [1] = "locked-1" }));

        Assert.Equal(
            """{"displayName":"Demo User","level":2,"canEdit":false,"isAdmin":true,"mustChangePassword":false,"salt":"salt","keys":{"1":"locked-1","2":"locked-2"}}""",
            user.ToContent().ToJsonString());
    }

    [Fact]
    public void TheAccount_IsTheIdOfTheDocument()
    {
        Assert.Equal(DocumentKey.InFolder("users", "demo.user"), DemoUser);
        Assert.Equal("demo.user", Read(DemoUser, User()).Account);
    }

    [Theory]
    // The levels of the keys are not checked against the level of the file: the admin list marks such a file (M4.6).
    [InlineData("0", "\"keys\":{}")]
    [InlineData("1", "\"keys\":{\"9\":\"k\"}")]
    [InlineData("9", "\"keys\":{\"1\":\"k\",\"2\":\"k\",\"3\":\"k\",\"4\":\"k\",\"5\":\"k\",\"6\":\"k\",\"7\":\"k\",\"8\":\"k\",\"9\":\"k\"}")]
    public void TheLevels_AndTheKeys_InTheirLimits_AreValid(string level, string keys)
    {
        var user = Read(DemoUser, User(level: level, keys: keys));

        Assert.Equal(int.Parse(level, System.Globalization.CultureInfo.InvariantCulture), user.Level);
    }

    [Fact]
    public void ADisplayName_Of100Characters_IsValid_AndOf101_IsInvalid()
    {
        Assert.Equal(100, Read(DemoUser, User(displayName: $"\"{new string('a', 100)}\"")).DisplayName.Length);
        Assert.Contains("displayName is blank or longer than 100", Invalid(User(displayName: $"\"{new string('a', 101)}\"")), StringComparison.Ordinal);
    }

    [Theory]
    // Not the types of the model.
    [InlineData("""{"displayName":"Demo User","level":"1","canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{}}""", "type")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":"false","isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{}}""", "type")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":[]}""", "type")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{"1":5}}""", "type")]
    // A missing member, also one in another case.
    [InlineData("""{"level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{}}""", "displayName is missing")]
    [InlineData("""{"DisplayName":"Demo User","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{}}""", "displayName is missing")]
    [InlineData("""{"displayName":"Demo User","canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{}}""", "level is missing")]
    [InlineData("""{"displayName":"Demo User","Level":9,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{}}""", "level is missing")]
    [InlineData("""{"displayName":"Demo User","level":1,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{}}""", "canEdit is missing")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"mustChangePassword":false,"salt":"salt","keys":{}}""", "isAdmin is missing")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"IsAdmin":true,"mustChangePassword":false,"salt":"salt","keys":{}}""", "isAdmin is missing")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"isAdmin":false,"salt":"salt","keys":{}}""", "mustChangePassword is missing")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"keys":{}}""", "salt is missing")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt"}""", "keys is missing")]
    // The values out of their limits.
    [InlineData("""{"displayName":"","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{}}""", "displayName is blank")]
    [InlineData("""{"displayName":" \t ","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{}}""", "displayName is blank")]
    [InlineData("""{"displayName":"Demo User","level":-1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{}}""", "level is not from 0 to 9")]
    [InlineData("""{"displayName":"Demo User","level":10,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{}}""", "level is not from 0 to 9")]
    // The names of the keys are the digits of the levels 1 to 9, and each has a value.
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{"x":"k"}}""", "keys has a name that is not the digit of a level from 1 to 9")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{"0":"k"}}""", "keys has a name that is not the digit")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{"01":"k"}}""", "keys has a name that is not the digit")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{"10":"k"}}""", "keys has a name that is not the digit")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{"+1":"k"}}""", "keys has a name that is not the digit")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{" 1":"k"}}""", "keys has a name that is not the digit")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{"١":"k"}}""", "keys has a name that is not the digit")]
    [InlineData("""{"displayName":"Demo User","level":1,"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt","keys":{"1":null}}""", "keys has no value for level 1")]
    public void AContentThatDoesNotFit_IsInvalid_WithAReason(string json, string reason)
    {
        Assert.Contains(reason, Invalid(JsonNode.Parse(json)!.AsObject()), StringComparison.Ordinal);
    }

    [Fact]
    public void TheConstructor_RefusesWhatDoesNotFit()
    {
        var keys = new LockedKeys("salt", new Dictionary<int, string> { [1] = "locked-1" });

        Assert.Throws<ArgumentException>(() => new UserFile("Demo.User", "Demo User", 1, false, false, false, keys));
        Assert.Throws<ArgumentException>(() => new UserFile("demo.user", " ", 1, false, false, false, keys));
        Assert.Throws<ArgumentException>(() => new UserFile("demo.user", "Demo User", 10, false, false, false, keys));
        Assert.Throws<ArgumentException>(() => new UserFile("demo.user", "Demo User", 1, false, false, false, new LockedKeys("salt", new Dictionary<int, string> { [0] = "k" })));
        Assert.Throws<ArgumentException>(() => new UserFile("demo.user", "Demo User", 1, false, false, false, new LockedKeys("salt", new Dictionary<int, string> { [10] = "k" })));
    }

    [Fact]
    public void TheModel_DoesNotChangeWithTheKeysItWasGiven()
    {
        var keys = new Dictionary<int, string> { [1] = "locked-1" };
        var user = new UserFile("demo.user", "Demo User", 1, false, false, false, new LockedKeys("salt", keys));

        keys[2] = "added";

        Assert.False(user.Keys.Keys.ContainsKey(2));
    }

    [Fact]
    public void ADocumentOutsideTheUsersFolder_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => UserFile.TryRead(DocumentKey.InFolder("records", "demo.user"), User(), out _, out _));
        Assert.Throws<ArgumentException>(() => UserFile.TryRead(DocumentKey.AtRoot("demo.user"), User(), out _, out _));
        Assert.Throws<ArgumentException>(() => UserFile.TryRead(DemoUser.InPrivate("demo.user"), User(), out _, out _));
    }

    /// <summary>A valid content with the given parts as JSON.</summary>
    private static JsonObject User(string displayName = "\"Demo User\"", string level = "1", string keys = "\"keys\":{\"1\":\"k\"}") =>
        JsonNode.Parse($$"""{"displayName":{{displayName}},"level":{{level}},"canEdit":false,"isAdmin":false,"mustChangePassword":false,"salt":"salt",{{keys}}}""")!.AsObject();

    private static UserFile Read(DocumentKey key, JsonObject content)
    {
        Assert.True(UserFile.TryRead(key, content, out var user, out var invalid), invalid);
        return user;
    }

    private static string Invalid(JsonObject content)
    {
        Assert.False(UserFile.TryRead(DemoUser, content, out var user, out var invalid));
        Assert.Null(user);
        return invalid;
    }
}
