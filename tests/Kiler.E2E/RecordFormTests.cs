using Kiler.DemoData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Kiler.E2E;

/// <summary>
/// The record form on the demo data (M6.6 to M6.8), as the demo admin: a new record, with a stash made from the form,
/// and its edit; and the "+" of a node of the tree, which makes a record at the node, or one level deeper with a new
/// stash. The names are made up for the test.
/// </summary>
[Trait("Category", "E2E")]
public class RecordFormTests(KilerBrowser browser)
{
    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task ANewRecord_AtAStashMadeFromTheForm_IsSavedAndEdited()
    {
        const string Stash = "E2E stash";
        const string Name = "E2E handbook link";
        const string Edited = "E2E handbook link, edited";
        const string Notes = "Edited by the E2E test.";
        var admin = DemoKiler.Admin;
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, admin);
        await using var page = await Demo.OpenSignedInAsync(browser, run, admin);
        var p = page.Page;

        // "+" above the tree opens the form without a path. The first stash is chosen, the second made from the form.
        await Demo.Tree(p).GetByRole(AriaRole.Link, new() { Name = "New record", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "New record", Level = 1 })).ToBeVisibleAsync();
        await Demo.ChooseTemplateAsync(p, "Link");
        await Demo.ChooseStashAsync(p, 1, "Hand", "Handbook");
        await Demo.CreateStashAsync(p, 2, Stash);
        await Demo.NameInput(p).FillAsync(Name);
        await p.GetByLabel("Address (required)", new() { Exact = true }).FillAsync("https://wiki.example.com/e2e");
        await p.GetByLabel("Kind", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "documentation" });
        var tags = p.GetByRole(AriaRole.Combobox, new() { Name = "Tags", Exact = true });
        await tags.FillAsync("e2e");
        await tags.PressAsync("Enter");
        await Expect(p.GetByRole(AriaRole.Button, new() { Name = "Remove the tag e2e", Exact = true })).ToBeVisibleAsync();
        await Demo.SaveAsync(p, Name);
        await Expect(Demo.PathLinks(p)).ToHaveTextAsync(["Handbook", Stash]);
        await Expect(Demo.Field(p, "Tags")).ToContainTextAsync("e2e");
        await Expect(Demo.Field(p, "Address")).ToContainTextAsync("https://wiki.example.com/e2e");

        // The edit starts from the record as it is, and keeps its path.
        await p.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Edit the record", Level = 1 })).ToBeVisibleAsync();
        await Expect(Demo.NameInput(p)).ToHaveValueAsync(Name);
        await Demo.NameInput(p).FillAsync(Edited);
        await p.GetByLabel("Notes", new() { Exact = true }).FillAsync(Notes);
        await Demo.SaveAsync(p, Edited);
        await Expect(Demo.Field(p, "Notes")).ToContainTextAsync(Notes);
        await Expect(Demo.PathLinks(p)).ToHaveTextAsync(["Handbook", Stash]);

        // The new stash is a node of the tree, under the one before it.
        await Demo.PathLinks(p).Filter(new() { HasText = Stash }).ClickAsync();
        await Expect(Demo.Count(p)).ToHaveTextAsync(Demo.Records(1));
        await Expect(Demo.RecordLink(p, Edited)).ToBeVisibleAsync();
        await Expect(Demo.Tree(p).GetByRole(AriaRole.Link, new() { Name = $"{Stash} (1)", Exact = true })).ToHaveAttributeAsync("aria-current", "true");

        await page.CloseAsync();
    }

    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task ThePlusOfANode_MakesARecordAtTheNode_AndOneLevelDeeperWithANewStash()
    {
        const string AtTheNode = "E2E note at the node";
        const string Deeper = "E2E note one level deeper";
        const string Stash = "E2E area";
        var admin = DemoKiler.Admin;
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, admin);
        await using var page = await Demo.OpenSignedInAsync(browser, run, admin);
        var p = page.Page;
        var tree = Demo.Tree(p);

        // The path of the node is kept until "Change", and the focus starts at the position after it.
        await tree.GetByRole(AriaRole.Button, new() { Name = "Open Billing", Exact = true }).ClickAsync();
        await tree.GetByRole(AriaRole.Link, new() { Name = "New record in Billing › Reports", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "New record", Level = 1 })).ToBeVisibleAsync();
        await Expect(p.GetByText("The path of the node is kept")).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Button, new() { Name = "Change", Exact = true })).ToBeVisibleAsync();
        await Expect(Demo.StashInput(p, 3)).ToBeFocusedAsync();
        await Demo.ChooseTemplateAsync(p, "Note");
        await Demo.NameInput(p).FillAsync(AtTheNode);
        await p.GetByLabel("Text", new() { Exact = true }).FillAsync("Made with the + of a node.");
        await Demo.SaveAsync(p, AtTheNode);
        await Expect(Demo.PathLinks(p)).ToHaveTextAsync(["Billing", "Reports"]);

        // One level deeper: a new stash at the position after the node.
        await Demo.ToMainViewAsync(p);
        await tree.GetByRole(AriaRole.Link, new() { Name = "New record in Orders", Exact = true }).ClickAsync();
        await Expect(Demo.StashInput(p, 2)).ToBeFocusedAsync();
        await Demo.CreateStashAsync(p, 2, Stash);
        await Demo.ChooseTemplateAsync(p, "Note");
        await Demo.NameInput(p).FillAsync(Deeper);
        await Demo.SaveAsync(p, Deeper);
        await Expect(Demo.PathLinks(p)).ToHaveTextAsync(["Orders", Stash]);

        await Demo.PathLinks(p).Filter(new() { HasText = Stash }).ClickAsync();
        await Expect(Demo.RecordLink(p, Deeper)).ToBeVisibleAsync();
        await Expect(tree.GetByRole(AriaRole.Link, new() { Name = $"{Stash} (1)", Exact = true })).ToHaveAttributeAsync("aria-current", "true");

        await page.CloseAsync();
    }
}
