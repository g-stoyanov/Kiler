using System.Text.RegularExpressions;
using Kiler.DemoData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Kiler.E2E;

/// <summary>
/// The template "Snippet" (PLAN M8, "Before the exam", item 4) on the demo data, as the demo editor: a snippet of the demo
/// shows its code highlighted in its language, and the form makes a snippet in another language through the picker of
/// its languages. The highlighting shows in the comment, in italics, which a code of plain text does not have.
/// </summary>
[Trait("Category", "E2E")]
public class SnippetTests(KilerBrowser browser)
{
    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task ASnippet_IsHighlightedInItsLanguage_AndTheFormMakesOneInAnother()
    {
        const string Name = "E2E greeting in Java";
        const string Typed = "System.out.println(\"Hello\");";
        var editor = DemoKiler.Editor;
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, editor);
        await using var page = await Demo.OpenSignedInAsync(browser, run, editor);
        var p = page.Page;

        // A snippet of the demo, in PowerShell. CodeMirror names the language that it highlights in on its content.
        await Demo.OpenRecordAsync(p, "Back up the photos");
        var code = Demo.Field(p, "Code").Locator(".cm-content");
        await Expect(code).ToHaveAttributeAsync("data-language", "powershell");
        await Expect(code).ToContainTextAsync("robocopy $source $target");
        await Expect(Comment(code, "# Copies")).ToHaveCSSAsync("font-style", "italic");

        // A new snippet starts as plain text, and the picker makes it Java.
        await Demo.ToMainViewAsync(p);
        await Demo.Tree(p).GetByRole(AriaRole.Link, new() { Name = "New record", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "New record", Level = 1 })).ToBeVisibleAsync();
        await Demo.ChooseTemplateAsync(p, "Snippet");
        await Demo.ChooseStashAsync(p, 1, "Hand", "Handbook");
        await Demo.NameInput(p).FillAsync(Name);
        var language = p.GetByLabel("Language of Code", new() { Exact = true });
        await Expect(language).ToHaveValueAsync("text");
        await language.SelectOptionAsync("java");
        await p.GetByRole(AriaRole.Textbox, new() { NameRegex = new Regex("^Code") }).ClickAsync();
        await p.Keyboard.TypeAsync("// Says hello.");
        await p.Keyboard.PressAsync("Enter");
        await p.Keyboard.TypeAsync(Typed);
        await Demo.SaveAsync(p, Name);

        await Expect(code).ToHaveAttributeAsync("data-language", "java");
        await Expect(code).ToContainTextAsync(Typed);
        await Expect(Comment(code, "// Says")).ToHaveCSSAsync("font-style", "italic");

        await page.CloseAsync();
    }

    /// <summary>The first token of the code whose text starts with <paramref name="start"/>: a comment, which the highlighting puts in italics.</summary>
    private static ILocator Comment(ILocator code, string start) =>
        code.Locator("span", new() { HasTextRegex = new Regex($"^{Regex.Escape(start)}") }).First;
}
