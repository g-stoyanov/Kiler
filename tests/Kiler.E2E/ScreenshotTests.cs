using System.Text.RegularExpressions;
using Kiler.DemoData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Kiler.E2E;

/// <summary>
/// The screenshots for the write-up (M6.13, M6.24, PLAN M8 step 5), on one demo Kiler in a window of 1280 × 800: the main
/// view with a search and a filter, the padlocks of a guest and a shown password, the form of a "+" with the stash picker
/// open, the code in light and dark, the conflict with its two versions, the audit of the shown password, and the stash
/// screen with a merge open. They go to <see cref="FolderVariable"/> (<c>artifacts\screenshots</c> from <c>e2e.ps1</c>,
/// which CI uploads), else <c>screenshots\</c> beside the test DLL. The data folder at the foot is blurred, as its path
/// holds the Windows account of the machine (<see cref="SaveAsync"/>). A write of the run does not hide the shown password,
/// as only a change of its record does (M6.16); the audit comes right after the password, whose "Show" writes its row, and
/// the conflict, which leaves the form in its panel, comes last.
/// </summary>
[Trait("Category", "E2E")]
public class ScreenshotTests(KilerBrowser browser)
{
    /// <summary>The folder for the screenshots; <c>e2e.ps1</c> sets it.</summary>
    public const string FolderVariable = "KILER_E2E_SCREENSHOTS";

    private const string DataFolder = "Data folder: ";

    private static readonly BrowserNewContextOptions Window = new()
    {
        ViewportSize = new() { Width = 1280, Height = 800 },
        ColorScheme = ColorScheme.Light,
    };

    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task TheScreensOfTheWriteUp_AreSavedFromTheDemoData()
    {
        const string Database = "Billing prod database";
        const string Query = "Unpaid invoices by month";
        const string Note = "Welcome to the team";
        const string Archive = "Billing archive";
        var admin = DemoKiler.Admin;
        var folder = Folder();
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, admin);
        await using var page = await browser.OpenAsync(run, Window);
        var p = page.Page;
        await Expect(DataFolderLine(p)).ToBeVisibleAsync();

        // A guest gets the padlocks of the prod user and password; the admin, signed in from one, shows the password.
        await Demo.OpenRecordAsync(p, Database);
        var password = Demo.Field(p, "Password");
        await Expect(password).ToContainTextAsync("requires level Prod");
        await SaveAsync(p, folder, "02-record-padlocks");
        await Demo.SignInAsync(p, admin, password.GetByRole(AriaRole.Link, new() { Name = "Sign in", Exact = true }));
        await Expect(Demo.Title(p, Database)).ToBeVisibleAsync();
        await password.GetByRole(AriaRole.Button, new() { Name = "Show Password", Exact = true }).ClickAsync();
        await Expect(password.Locator(".secret-shown")).ToBeVisibleAsync();
        await SaveAsync(p, folder, "03-record-password");
        await password.GetByRole(AriaRole.Button, new() { Name = "Hide Password", Exact = true }).ClickAsync();

        // That "Show" wrote a line of the audit, so the audit screen of today has its row.
        await HeaderLink(p, "Audit").ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Audit", Level = 1 })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Main).GetByRole(AriaRole.Row).Filter(new() { HasText = Database })).ToHaveCountAsync(1);
        await SaveAsync(p, folder, "08-audit");

        // The main view: a node of the tree open, a search, and a type among its results.
        await Demo.ToMainViewAsync(p);
        var tree = Demo.Tree(p);
        await tree.GetByRole(AriaRole.Button, new() { Name = "Open Billing", Exact = true }).ClickAsync();
        await p.GetByRole(AriaRole.Searchbox, new() { Name = "Search" }).FillAsync("billing");
        await Expect(Demo.RecordLink(p, "Orders prod database")).ToHaveCountAsync(0);
        // Playwright gives the pattern to the page between slashes and does not escape one inside it.
        var type = p.GetByRole(AriaRole.Region, new() { Name = "Filters" })
            .GetByRole(AriaRole.Link, new() { NameRegex = new Regex(@"^Server \/ database \(\d+\)$") });
        await type.ClickAsync();
        await Expect(type).ToHaveAttributeAsync("aria-current", "true");
        await Expect(Demo.RecordLink(p, "Billing month-end close")).ToHaveCountAsync(0);
        await Expect(Demo.RecordLink(p, Database)).ToBeVisibleAsync();
        await SaveAsync(p, folder, "01-main-view");

        // The "+" of a node keeps its path, and the next position lists the stashes used under it first.
        await Demo.ToMainViewAsync(p);
        await tree.GetByRole(AriaRole.Link, new() { Name = "New record in Billing", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "New record", Level = 1 })).ToBeVisibleAsync();
        await Demo.ChooseTemplateAsync(p, "Server / database");
        await Demo.NameInput(p).FillAsync("Billing staging database");
        await Demo.StashInput(p, 2).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Listbox, new() { Name = "Stash 2", Exact = true })).ToBeVisibleAsync();
        await SaveAsync(p, folder, "04-form-stash-picker");

        // "Create …" makes the stash at once, so the form left unsaved leaves a stash without records, the one row of the
        // stash screen with "Delete"; its merge, open, shows the choice of another stash below the table.
        await Demo.CreateStashAsync(p, 2, Archive);
        await HeaderLink(p, "Stashes").ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Stashes", Level = 1 })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Button, new() { Name = $"Delete {Archive}", Exact = true })).ToBeVisibleAsync();
        await p.GetByRole(AriaRole.Button, new() { Name = $"Merge {Archive} into…", Exact = true }).ClickAsync();
        await Expect(p.GetByLabel("Merge into", new() { Exact = true })).ToBeFocusedAsync();
        await SaveAsync(p, folder, "09-stashes");

        // The code of a query in the record view, in light, then in dark once the editor has taken its colours.
        await Demo.ToMainViewAsync(p);
        await Demo.OpenRecordAsync(p, Query);
        await Expect(p.Locator(".cm-content")).ToContainTextAsync("FROM invoices i");
        var editor = p.Locator(".cm-editor");
        var light = await editor.EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor");
        await SaveAsync(p, folder, "05-code-light");
        var theme = p.GetByLabel("Theme");
        await theme.SelectOptionAsync(new SelectOptionValue { Label = "Dark" });
        await Expect(p.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        await Expect(editor).Not.ToHaveCSSAsync("background-color", light);
        await SaveAsync(p, folder, "06-code-dark");
        await theme.SelectOptionAsync(new SelectOptionValue { Label = "Light" });
        await Expect(p.Locator("html")).ToHaveAttributeAsync("data-theme", "light");

        // A second page saves the record while the first holds its form, whose save then gets the conflict.
        await Demo.ToMainViewAsync(p);
        await Demo.OpenRecordAsync(p, Note);
        await p.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }).ClickAsync();
        var editing = p.GetByRole(AriaRole.Heading, new() { Name = "Edit the record", Level = 1 });
        await Expect(editing).ToBeVisibleAsync();
        var second = await p.Context.NewPageAsync();
        await second.GotoAsync(p.Url);
        await Expect(second.GetByRole(AriaRole.Heading, new() { Name = "Edit the record", Level = 1 })).ToBeVisibleAsync();
        await Demo.NameInput(second).FillAsync($"{Note}: start here");
        await Demo.SaveAsync(second, $"{Note}: start here");
        await Demo.NameInput(p).FillAsync($"{Note}: your first week");
        await p.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(p.Locator(".form-conflict")).ToContainTextAsync($"{admin.DisplayName} changed the record at");
        await SaveAsync(p, folder, "07-conflict");

        await page.CloseAsync();
    }

    /// <summary>The folder of <see cref="FolderVariable"/>, else <c>screenshots\</c> beside the test DLL.</summary>
    private static string Folder()
    {
        var folder = Environment.GetEnvironmentVariable(FolderVariable) is { Length: > 0 } set
            ? set
            : Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A link of the header, by its whole name.</summary>
    private static ILocator HeaderLink(IPage p, string name) =>
        p.GetByRole(AriaRole.Banner).GetByRole(AriaRole.Link, new() { Name = name, Exact = true });

    /// <summary>The line of the data folder at the foot of the frame.</summary>
    private static ILocator DataFolderLine(IPage p) => p.Locator(".frame-foot > span").Filter(new() { HasText = DataFolder });

    /// <summary>
    /// Saves the whole page as <paramref name="name"/><c>.png</c>, with the mouse moved off the control it last clicked, so
    /// no hover shows, and the data folder blurred, not hidden, so its line still shows. The options of Playwright that
    /// hide the caret, stop the animations or add a style put a <c>&lt;style&gt;</c> into the page, which the policy
    /// refuses, so the blur goes through the CSSOM, which it allows (kiler-ui, "The embedded build").
    /// </summary>
    private static async Task SaveAsync(IPage p, string folder, string name)
    {
        await p.Mouse.MoveAsync(0, 0);
        await DataFolderLine(p).EvaluateAsync("element => { element.style.filter = 'blur(5px)'; }");
        await p.ScreenshotAsync(new()
        {
            Path = Path.Combine(folder, $"{name}.png"),
            FullPage = true,
            Caret = ScreenshotCaret.Initial,
        });
    }
}
