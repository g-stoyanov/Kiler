using System.Text.Json.Nodes;

namespace Kiler.Core.Tests;

public class SettingsFileTests
{
    [Fact]
    public void SettingsFile_ReadsWhatItWrites_ThroughTheDataLayer()
    {
        using var data = new TestData();
        var settings = new SettingsFile(guestMode: true, autoLockMinutes: 30);

        var document = data.WriteAndRead(SettingsFile.Key, settings.ToContent());

        Assert.True(SettingsFile.TryRead(document, out var read, out var invalid), invalid);
        Assert.True(read.GuestMode);
        Assert.Equal(30, read.AutoLockMinutes);
        Assert.Equal("""{"guestMode":true,"autoLockMinutes":30}""", read.ToContent().ToJsonString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(SettingsFile.MaxAutoLockMinutes)]
    public void TheMinutes_From1To1440_AreValid(int minutes)
    {
        Assert.True(SettingsFile.TryRead(SettingsFile.Key, Content($$"""{"guestMode":false,"autoLockMinutes":{{minutes}}}"""), out var settings, out var invalid), invalid);
        Assert.Equal(minutes, settings.AutoLockMinutes);
    }

    [Theory]
    [InlineData("""{"guestMode":"true","autoLockMinutes":30}""", "type")]
    [InlineData("""{"guestMode":1,"autoLockMinutes":30}""", "type")]
    [InlineData("""{"guestMode":false,"autoLockMinutes":"30"}""", "type")]
    [InlineData("""{"guestMode":false,"autoLockMinutes":1.5}""", "type")]
    [InlineData("""{"guestMode":false,"autoLockMinutes":99999999999}""", "type")]
    [InlineData("""{"autoLockMinutes":30}""", "guestMode is missing")]
    [InlineData("""{"GuestMode":false,"autoLockMinutes":30}""", "guestMode is missing")]
    [InlineData("""{"guestMode":null,"autoLockMinutes":30}""", "guestMode is missing")]
    [InlineData("""{"guestMode":false}""", "autoLockMinutes is missing")]
    [InlineData("""{"guestMode":false,"autolockminutes":30}""", "autoLockMinutes is missing")]
    [InlineData("""{"guestMode":false,"autoLockMinutes":0}""", "autoLockMinutes is not from 1 to 1440")]
    [InlineData("""{"guestMode":false,"autoLockMinutes":-30}""", "autoLockMinutes is not from 1 to 1440")]
    [InlineData("""{"guestMode":false,"autoLockMinutes":1441}""", "autoLockMinutes is not from 1 to 1440")]
    public void AContentThatDoesNotFit_IsInvalid_WithAReason(string json, string reason)
    {
        Assert.False(SettingsFile.TryRead(SettingsFile.Key, Content(json), out var settings, out var invalid));
        Assert.Null(settings);
        Assert.Contains(reason, invalid, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SettingsFile.MaxAutoLockMinutes + 1)]
    public void TheConstructor_RefusesMinutesOutOfTheirLimits(int minutes)
    {
        Assert.Throws<ArgumentException>(() => new SettingsFile(guestMode: false, minutes));
    }

    [Fact]
    public void AnotherDocument_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => SettingsFile.TryRead(LevelsFile.Key, Content("""{"guestMode":false,"autoLockMinutes":30}"""), out _, out _));
    }

    private static JsonObject Content(string json) => JsonNode.Parse(json)!.AsObject();
}
