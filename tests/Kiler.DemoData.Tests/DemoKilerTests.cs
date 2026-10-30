using System.Globalization;
using Kiler.Core;

namespace Kiler.DemoData.Tests;

/// <summary>
/// The demo Kiler (M4.10, M5.5): the data folder that <see cref="DemoKiler.Make"/> writes opens through a new local copy, as
/// Kiler opens it at its first start; each demo user signs in with their demo password and reads the fields of the demo
/// records at their levels, and the guest those of level 0.
/// </summary>
public sealed class DemoKilerTests(DemoKilerTests.Made made) : IClassFixture<DemoKilerTests.Made>
{
    public static TheoryData<string> Accounts => [.. DemoKiler.Users.Select(user => user.Account)];

    /// <summary>The demo users, and an empty account for the guest: <see cref="TheoryData{T}"/> takes no <c>null</c>.</summary>
    public static TheoryData<string> Readers => ["", .. DemoKiler.Users.Select(user => user.Account)];

    [Fact]
    public void Make_WritesADataFolderThatOpens_WithTheDemoLevelsSettingsAndUsers()
    {
        var files = made.Files;

        var levels = Assert.IsType<LevelsFile>(files.Levels?.Model);
        Assert.Equal(DemoKiler.LevelNames, levels.Levels.Select(level => level.Name));
        var settings = Assert.IsType<SettingsFile>(files.Settings?.Model);
        Assert.Equal((true, 30), (settings.GuestMode, settings.AutoLockMinutes));
        Assert.Equal(DemoKiler.Users.Select(user => user.Account).Order(StringComparer.Ordinal), files.Users.Keys);
        Assert.All(files.Users.Values, file => Assert.True(file.IsValid, file.Invalid));

        // The initial templates come with the wizard (M5.2), at the levels of the demo, 0 to 3.
        Assert.Equal(["db", "link", "note", "sql"], files.Templates.Keys);
        Assert.All(files.Templates.Values, file => Assert.True(file.IsValid, file.Invalid));
        var password = Assert.Single(Assert.IsType<TemplateFile>(files.Templates["db"].Model).Fields, field => field.Key == "password");
        Assert.Equal(3, password.LevelByEnv?["prod"]);
    }

    [Theory]
    [MemberData(nameof(Accounts))]
    public void EachDemoUser_SignsInWithTheirDemoPassword_WithTheirLevelAndRights_AndNoChange(string account)
    {
        var user = Assert.Single(DemoKiler.Users, user => user.Account == account);

        using var session = made.SignIn(user);

        // Level 0 has no key, and every session reads it.
        Assert.Equal(Enumerable.Range(0, user.Level + 1), session.Levels);
        Assert.Equal((user.DisplayName, user.CanEdit, user.IsAdmin, false), (session.DisplayName, session.CanEdit, session.IsAdmin, session.MustChangePassword));
    }

    [Fact]
    public void ADemoUser_WithAnotherDemoPassword_DoesNotSignIn()
    {
        Assert.False(SignIn.TryStart(made.Files, "demo.reader", DemoKiler.Admin.Password, out _, out var refusal));
        Assert.Equal(SignInRefusal.WrongPassword, refusal);
    }

    [Fact]
    public void Make_WritesTheDemoStashesAndRecords_EachInItsPath_ByItsAuthor()
    {
        var set = made.Read(null);

        var stashes = set.Stashes.Values.Select(stash => Assert.IsType<StashFile>(stash.Model)).ToDictionary(stash => stash.Id);
        Assert.Equal(
            DemoRecords.Stashes.Select(stash => $"{stash.Name} by {stash.Author}").Order(StringComparer.Ordinal),
            stashes.Values.Select(stash => $"{stash.Name} by {stash.Authorship.CreatedBy}").Order(StringComparer.Ordinal));
        var files = set.Records.Values.Select(view => Assert.IsType<RecordFile>(view.File.Model)).ToList();
        Assert.Equal(DemoRecords.Set.Count, files.Count);
        foreach (var demo in DemoRecords.Set)
        {
            var file = Assert.Single(files, file => file.Name == demo.Name);
            Assert.Equal((demo.Name, demo.Template, demo.Author), (file.Name, file.Template, file.Authorship.CreatedBy));
            Assert.Equal(demo.Path, file.Stash.Select(id => stashes[id].Name));
            Assert.Equal(demo.Tags, file.Tags);

            // A field raised by its author is in the group of the level asked.
            Assert.All(demo.Levels, pair => Assert.Contains(pair.Key, file.Fields.GetValueOrDefault(pair.Value) ?? []));
        }

        // What the demo is for: each template, paths of 1 to 4 stashes, groups of the levels 1 to 3, and records of the editor.
        Assert.Equal(["db", "link", "note", "sql"], files.Select(file => file.Template).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal([1, 2, 3, 4], files.Select(file => file.Stash.Count).Distinct().Order());
        Assert.Equal([1, 2, 3], files.SelectMany(file => file.Protected.Keys).Distinct().Order());
        Assert.Contains(files, file => file.Authorship.CreatedBy == DemoKiler.Editor.Account);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void EachDemoUserAndTheGuest_ReadTheFieldsOfTheirLevels_AndAPadlockOnEachAbove(string account)
    {
        var user = DemoKiler.Users.SingleOrDefault(user => user.Account == account);
        var level = user?.Level ?? 0;
        using var session = user is null ? null : made.SignIn(user);

        var set = made.Read(session);

        var names = set.Records.Values.Select(view => view.File.Model!).ToDictionary(file => file.Id, file => file.Name);
        var read = new SortedSet<int>();
        var locked = new SortedSet<int>();
        foreach (var demo in DemoRecords.Set)
        {
            var view = Assert.Single(set.Records.Values, view => view.File.Model?.Name == demo.Name);
            var template = Assert.IsType<TemplateFile>(view.Template);
            foreach (var field in view.Fields.Prepend(Assert.IsType<RecordField>(view.Description)))
            {
                var expected = field.Key == RecordFile.Description ? demo.Description : demo.Refs.GetValueOrDefault(field.Key) ?? Show(demo.Fields.GetValueOrDefault(field.Key));
                if (expected is null)
                {
                    Assert.Equal((demo.Name, field.Key, FieldKind.Empty), (demo.Name, field.Key, field.Kind));
                }
                else if (field.Level > level)
                {
                    Assert.Equal((demo.Name, field.Key, FieldKind.Locked), (demo.Name, field.Key, field.Kind));
                    locked.Add(field.Level);
                }
                else if (template.Fields.Any(templateField => templateField.Key == field.Key && templateField.Type == FieldType.Secret))
                {
                    // A secret is never kept in what is read, and never printed here (SKILL.md, "Tests").
                    Assert.Equal((demo.Name, field.Key, FieldKind.Secret), (demo.Name, field.Key, field.Kind));
                    read.Add(field.Level);
                }
                else
                {
                    var actual = demo.Refs.ContainsKey(field.Key) ? names.GetValueOrDefault(field.Value?.Text ?? "") : Show(field.Value);
                    Assert.Equal((demo.Name, field.Key, FieldKind.Value, expected), (demo.Name, field.Key, field.Kind, actual));
                    read.Add(field.Level);
                }
            }
        }

        // The demo has values at every level: each one that the reader holds is read, and each above it has a padlock.
        Assert.Equal(Enumerable.Range(0, level + 1), read);
        Assert.Equal(Enumerable.Range(level + 1, DemoKiler.Admin.Level - level), locked);
    }

    /// <summary>A value as a text, to compare: a number in the invariant culture, a code as <c>language: code</c>.</summary>
    private static string? Show(FieldValue? value) => value switch
    {
        { Kind: FieldValueKind.Number } => value.Number.ToString(CultureInfo.InvariantCulture),
        { Kind: FieldValueKind.Code } => $"{value.Language}: {value.Text}",
        _ => value?.Text,
    };

    public sealed class Made() : DemoFolder(volume: false);
}
