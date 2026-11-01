using System.Globalization;
using Kiler.DemoData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Kiler.E2E;

/// <summary>
/// The stash screen (M6.23) on the demo data, as the demo admin: a rename that the tree shows, a stash made from the form
/// and merged into another, whose record then shows there, and the delete of a stash that no record uses. The names of
/// the new stashes and records are made up for the test.
/// </summary>
[Trait("Category", "E2E")]
public class StashesTests(KilerBrowser browser)
{
    private const string Handbook = "Handbook";

    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task TheAdminRenamesAStash_AndTheTreeShowsTheNewName()
    {
        const string NewName = "Team handbook";
        var records = InHandbook();
        var admin = DemoKiler.Admin;
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, admin);
        await using var page = await Demo.OpenSignedInAsync(browser, run, admin);
        var p = page.Page;

        await OpenStashesAsync(p);
        await Expect(Records(p, Handbook)).ToHaveTextAsync(Number(records));
        await p.GetByRole(AriaRole.Button, new() { Name = $"Rename {Handbook}", Exact = true }).ClickAsync();
        var name = p.GetByLabel("New name", new() { Exact = true });
        await Expect(name).ToBeFocusedAsync();
        await name.FillAsync(NewName);
        await p.GetByRole(AriaRole.Button, new() { Name = "Rename", Exact = true }).ClickAsync();
        await Expect(p.GetByText($"“{Handbook}” is now “{NewName}”.")).ToBeVisibleAsync();
        await Expect(Records(p, NewName)).ToHaveTextAsync(Number(records));
        await Expect(Row(p, Handbook)).ToHaveCountAsync(0);

        // Every path reads the stash by its id, so the tree has the new name at once.
        await Demo.ToMainViewAsync(p);
        var tree = Demo.Tree(p);
        await Expect(tree.GetByRole(AriaRole.Link, new() { Name = $"{NewName} ({records})", Exact = true })).ToBeVisibleAsync();
        await Expect(tree.GetByRole(AriaRole.Link, new() { Name = $"{Handbook} ({records})", Exact = true })).ToHaveCountAsync(0);

        await page.CloseAsync();
    }

    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task AStashMadeFromTheForm_IsMergedIntoAnother_AndItsRecordShowsThere()
    {
        const string Stash = "E2E area";
        const string Name = "E2E note to merge";
        var records = InHandbook();
        var admin = DemoKiler.Admin;
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, admin);
        await using var page = await Demo.OpenSignedInAsync(browser, run, admin);
        var p = page.Page;

        await NewRecordAsync(p);
        await Demo.ChooseTemplateAsync(p, "Note");
        await Demo.CreateStashAsync(p, 1, Stash);
        await Demo.NameInput(p).FillAsync(Name);
        await Demo.SaveAsync(p, Name);
        await Expect(Demo.PathLinks(p)).ToHaveTextAsync([Stash]);

        // The choices of the merge are the other stashes, without the stash itself.
        await OpenStashesAsync(p);
        await Expect(Records(p, Stash)).ToHaveTextAsync("1");
        await p.GetByRole(AriaRole.Button, new() { Name = $"Merge {Stash} into…", Exact = true }).ClickAsync();
        var into = p.GetByLabel("Merge into", new() { Exact = true });
        await Expect(into).ToBeFocusedAsync();
        await Expect(into.GetByRole(AriaRole.Option, new() { Name = Handbook, Exact = true })).ToHaveCountAsync(1);
        await Expect(into.GetByRole(AriaRole.Option, new() { Name = Stash, Exact = true })).ToHaveCountAsync(0);
        await into.SelectOptionAsync(new SelectOptionValue { Label = Handbook });
        await p.GetByRole(AriaRole.Button, new() { Name = "Merge", Exact = true }).ClickAsync();
        await Expect(p.GetByText($"“{Stash}” is merged into “{Handbook}”.")).ToBeVisibleAsync();
        await Expect(Row(p, Stash)).ToHaveCountAsync(0);
        await Expect(Records(p, Handbook)).ToHaveTextAsync(Number(records + 1));

        // The record is not written again, and shows at the stash it was merged into.
        await Demo.ToMainViewAsync(p);
        await Demo.Tree(p).GetByRole(AriaRole.Link, new() { Name = $"{Handbook} ({records + 1})", Exact = true }).ClickAsync();
        await Expect(Demo.Count(p)).ToHaveTextAsync(Demo.Records(records + 1));
        await Demo.OpenRecordAsync(p, Name);
        await Expect(Demo.PathLinks(p)).ToHaveTextAsync([Handbook]);

        await page.CloseAsync();
    }

    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task AStashThatNoRecordUses_IsDeleted_AndAUsedOneHasNoDelete()
    {
        const string Stash = "E2E unused";
        var admin = DemoKiler.Admin;
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, admin);
        await using var page = await Demo.OpenSignedInAsync(browser, run, admin);
        var p = page.Page;

        // "Create …" in the form makes the stash at once, so leaving the form unsaved leaves a stash without records.
        await NewRecordAsync(p);
        await Demo.CreateStashAsync(p, 1, Stash);

        await OpenStashesAsync(p);
        await Expect(Records(p, Stash)).ToHaveTextAsync("0");
        await Expect(p.GetByRole(AriaRole.Button, new() { Name = $"Delete {Handbook}", Exact = true })).ToHaveCountAsync(0);
        await p.GetByRole(AriaRole.Button, new() { Name = $"Delete {Stash}", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true })).ToBeFocusedAsync();
        await p.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(p.GetByText($"“{Stash}” is deleted.")).ToBeVisibleAsync();
        await Expect(Row(p, Stash)).ToHaveCountAsync(0);

        // The form no longer offers it, so its name can be taken again.
        await Demo.ToMainViewAsync(p);
        await NewRecordAsync(p);
        await Demo.StashInput(p, 1).FillAsync(Stash);
        await Expect(p.GetByRole(AriaRole.Option, new() { Name = $"Create “{Stash}”", Exact = true })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Option, new() { Name = Stash, Exact = true })).ToHaveCountAsync(0);

        await page.CloseAsync();
    }

    /// <summary>The shared records of the demo whose path has the stash <see cref="Handbook"/>.</summary>
    private static int InHandbook() => DemoRecords.Set.Count(record => record.Path.Contains(Handbook));

    private static string Number(int count) => count.ToString(CultureInfo.InvariantCulture);

    /// <summary>Opens the stash screen with the link of the header.</summary>
    private static async Task OpenStashesAsync(IPage p)
    {
        await p.GetByRole(AriaRole.Banner).GetByRole(AriaRole.Link, new() { Name = "Stashes", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Stashes", Level = 1 })).ToBeVisibleAsync();
    }

    /// <summary>Opens the form of a new record with the "+" above the tree of the main view.</summary>
    private static async Task NewRecordAsync(IPage p)
    {
        await Demo.Tree(p).GetByRole(AriaRole.Link, new() { Name = "New record", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "New record", Level = 1 })).ToBeVisibleAsync();
    }

    /// <summary>The row of the stash screen of the stash <paramref name="name"/>, by the whole name.</summary>
    private static ILocator Row(IPage p, string name) =>
        p.GetByRole(AriaRole.Main).GetByRole(AriaRole.Row).Filter(new() { Has = p.GetByRole(AriaRole.Cell, new() { Name = name, Exact = true }) });

    /// <summary>The count of the shared records in the row of the stash <paramref name="name"/>.</summary>
    private static ILocator Records(IPage p, string name) => Row(p, name).GetByRole(AriaRole.Cell).Nth(1);
}
