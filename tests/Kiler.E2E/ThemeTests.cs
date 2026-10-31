using Kiler.DemoData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Kiler.E2E;

/// <summary>
/// The theme (M1.7) with a SQL of the demo data in the record view and in the form (M6.9), so the check of the policy
/// covers CodeMirror in its shadow root in both themes, and the typing into its editor: text typed at the end of the
/// query must stay at its end (the M6.9 entry of <c>code.ts</c> under "Left for after the exam"). The choice is kept for
/// the next page of the context, as it is in <c>localStorage</c> until the personal configuration.
/// </summary>
[Trait("Category", "E2E")]
public class ThemeTests(KilerBrowser browser)
{
    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task TheTheme_ChangesTheCodeInTheViewAndTheForm_AndStaysForTheNextPage()
    {
        const string RecordName = "Unpaid invoices by month";
        const string Typed = "FETCH FIRST 12 ROWS ONLY";
        var admin = DemoKiler.Admin;
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, admin);
        await using var page = await Demo.OpenSignedInAsync(browser, run, admin);
        var p = page.Page;
        var theme = p.GetByLabel("Theme");

        await ChooseAsync(p, theme, "Dark", "dark");
        await Demo.OpenRecordAsync(p, RecordName);
        var code = p.Locator(".cm-content");
        await Expect(code).ToContainTextAsync("FROM invoices i");

        // The colours of the editor follow the theme while it is open.
        await p.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Edit the record", Level = 1 })).ToBeVisibleAsync();
        var editor = p.Locator(".cm-editor");
        await Expect(editor).ToBeVisibleAsync();
        var dark = await editor.EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor");
        await ChooseAsync(p, theme, "Light", "light");
        await Expect(editor).Not.ToHaveCSSAsync("background-color", dark);

        await p.GetByRole(AriaRole.Textbox, new() { Name = "Query", Exact = true }).ClickAsync();
        await p.Keyboard.PressAsync("Control+End");
        await p.Keyboard.PressAsync("Enter");
        await p.Keyboard.TypeAsync(Typed);
        await Demo.SaveAsync(p, RecordName);
        await Expect(code).ToContainTextAsync(Typed);
        var saved = await code.InnerTextAsync();
        Assert.True(saved.TrimEnd().EndsWith(Typed, StringComparison.Ordinal), $"The text typed at the end of the query is not at its end:{Environment.NewLine}{saved}");

        // A new page of the context starts with the theme chosen last.
        await ChooseAsync(p, theme, "Dark", "dark");
        var next = await p.Context.NewPageAsync();
        await next.GotoAsync(page.Root);
        await Expect(next.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        await Expect(next.GetByLabel("Theme")).ToHaveValueAsync("dark");

        await page.CloseAsync();
    }

    /// <summary>Chooses a theme in the header and checks the root, whose <c>color-scheme</c> picks the side of every colour.</summary>
    private static async Task ChooseAsync(IPage p, ILocator theme, string label, string value)
    {
        await theme.SelectOptionAsync(new SelectOptionValue { Label = label });
        await Expect(p.Locator("html")).ToHaveAttributeAsync("data-theme", value);
        Assert.Equal(value, await p.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).colorScheme"));
    }
}
