using Kiler.DemoData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Kiler.E2E;

/// <summary>
/// The eleven screenshots for the write-up and the README (M6.13, M6.24, PLAN M8 step 5 and "Before the exam", item 4), on
/// one demo Kiler in a window of 1280 × 800, each of the whole page with its header: the main view with a search and the
/// tree, the padlocks of a guest and a shown password, the form of a "+" with the stash picker open, a query with its
/// "Parameters", the history of a record that two users changed, the conflict with its two versions, the trash, the audit
/// of the shown and copied passwords, the users in the dark theme, and a snippet highlighted in Python. They go to
/// <see cref="FolderVariable"/> (<c>artifacts\screenshots</c> from <c>e2e.ps1</c>, which CI uploads), else
/// <c>screenshots\</c> beside the test DLL. The data folder at the foot is blurred, as its path holds the Windows account
/// of the machine (<see cref="SaveAsync"/>). A write of the run does not hide the shown password, as only a change of its
/// record does (M6.16); the audit comes after the "Show" and "Copy" that write its rows, and the conflict, which leaves the
/// form in its panel, comes last.
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
        const string Database = "Library database";
        const string OtherDatabase = "Fitness database";
        const string Query = "Overdue loans by branch";
        const string Snippet = "Resize the photos for the blog";
        const string EditorsNote = "Access requests";
        const string Note = "Welcome to the team";
        string[] deleted = ["Central logs", "Photo blog uptime check"];
        var admin = DemoKiler.Admin;
        var folder = Folder();
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, admin);
        await using var page = await browser.OpenAsync(run, Window);
        var p = page.Page;
        await p.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        await Expect(DataFolderLine(p)).ToBeVisibleAsync();

        // A guest gets the padlocks of the prod user and password; the admin, signed in from one, shows the password.
        await Demo.OpenRecordAsync(p, Database);
        var password = Demo.Field(p, "Password");
        await Expect(password).ToContainTextAsync("requires level Secret");
        await SaveAsync(p, folder, "02-record-padlocks");
        await Demo.SignInAsync(p, admin, password.GetByRole(AriaRole.Link, new() { Name = "Sign in", Exact = true }));
        await Expect(Demo.Title(p, Database)).ToBeVisibleAsync();
        await password.GetByRole(AriaRole.Button, new() { Name = "Show Password", Exact = true }).ClickAsync();
        await Expect(password.Locator(".secret-shown")).ToBeVisibleAsync();
        await SaveAsync(p, folder, "03-record-password");
        var copy = password.GetByRole(AriaRole.Button, new() { Name = "Copy Password", Exact = true });
        await copy.ClickAsync();
        await Expect(copy).ToHaveTextAsync("Copied");
        await password.GetByRole(AriaRole.Button, new() { Name = "Hide Password", Exact = true }).ClickAsync();

        // The password of another prod database, shown too, so the audit has the rows of two records.
        await Demo.ToMainViewAsync(p);
        await Demo.OpenRecordAsync(p, OtherDatabase);
        var other = Demo.Field(p, "Password");
        await other.GetByRole(AriaRole.Button, new() { Name = "Show Password", Exact = true }).ClickAsync();
        await Expect(other.Locator(".secret-shown")).ToBeVisibleAsync();
        await other.GetByRole(AriaRole.Button, new() { Name = "Hide Password", Exact = true }).ClickAsync();

        // That "Show" and "Copy" wrote lines of the audit, so the audit screen of today has their rows.
        await HeaderLink(p, "Audit").ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Audit", Level = 1 })).ToBeVisibleAsync();
        var auditRows = p.GetByRole(AriaRole.Main).GetByRole(AriaRole.Row);
        await Expect(auditRows.Filter(new() { HasText = Database })).ToHaveCountAsync(2);
        await Expect(auditRows.Filter(new() { HasText = OtherDatabase })).ToHaveCountAsync(1);
        await SaveAsync(p, folder, "09-audit");

        // The main view: a node of the tree open and a search, whose results are of each template.
        await Demo.ToMainViewAsync(p);
        var tree = Demo.Tree(p);
        await tree.GetByRole(AriaRole.Button, new() { Name = "Open Library app", Exact = true }).ClickAsync();
        var search = p.GetByRole(AriaRole.Searchbox, new() { Name = "Search" });
        await search.FillAsync("library");
        await Expect(Demo.RecordLink(p, OtherDatabase)).ToHaveCountAsync(0);
        await Expect(Demo.RecordLink(p, "Monthly report to the library")).ToBeVisibleAsync();
        await Expect(Demo.RecordLink(p, Database)).ToBeVisibleAsync();
        await search.BlurAsync();
        await SaveAsync(p, folder, "01-main-view");

        // The "+" of a node keeps its path, and the next position lists the stashes used under it first.
        await Demo.ToMainViewAsync(p);
        await tree.GetByRole(AriaRole.Link, new() { Name = "New record in Library app", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "New record", Level = 1 })).ToBeVisibleAsync();
        await Demo.ChooseTemplateAsync(p, "Server / database");
        await Demo.NameInput(p).FillAsync("Library archive database");
        await Demo.StashInput(p, 2).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Listbox, new() { Name = "Stash 2", Exact = true })).ToBeVisibleAsync();
        await SaveAsync(p, folder, "04-form-stash-picker");

        // A query in the record view, with a value for its parameter, which "Parameters" puts into the copy.
        await Demo.ToMainViewAsync(p);
        await Demo.OpenRecordAsync(p, Query);
        await Expect(p.Locator(".cm-content")).ToContainTextAsync("FROM loans l");
        await p.GetByRole(AriaRole.Button, new() { Name = "Parameters", Exact = true }).ClickAsync();
        var branch = p.GetByLabel(":branch", new() { Exact = true });
        await branch.FillAsync("3");
        await branch.BlurAsync();
        await SaveAsync(p, folder, "05-code-parameters");

        // A snippet, highlighted in its language.
        await Demo.ToMainViewAsync(p);
        await Demo.OpenRecordAsync(p, Snippet);
        await Expect(Demo.Field(p, "Code").Locator(".cm-content")).ToHaveAttributeAsync("data-language", "python");
        await Expect(p.Locator(".cm-content")).ToContainTextAsync("image.thumbnail((1600, 1600))");
        await SaveAsync(p, folder, "11-snippet");

        // A note of the editor that the admin changes twice has three versions by two users.
        await Demo.ToMainViewAsync(p);
        await Demo.OpenRecordAsync(p, EditorsNote);
        string[] texts =
        [
            "Ask for access to a sandbox database in the service desk, with the name of the record in Kiler. The team lead approves it.",
            "Ask for access to a sandbox database in the service desk, with the name of the record in Kiler. The team lead approves it within a day.",
        ];
        foreach (var text in texts)
        {
            await p.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }).ClickAsync();
            await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Edit the record", Level = 1 })).ToBeVisibleAsync();
            await p.GetByLabel("Text", new() { Exact = true }).FillAsync(text);
            await Demo.SaveAsync(p, EditorsNote);
        }

        await p.GetByRole(AriaRole.Link, new() { Name = "History", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "History", Level = 1 })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Cell, new() { Name = "3 (current)", Exact = true })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Cell, new() { Name = DemoKiler.Editor.DisplayName, Exact = true })).ToBeVisibleAsync();
        await SaveAsync(p, folder, "06-history");

        // Two records deleted wait in the trash.
        foreach (var name in deleted)
        {
            await Demo.ToMainViewAsync(p);
            await Demo.OpenRecordAsync(p, name);
            await p.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
            await p.GetByRole(AriaRole.Group, new() { Name = "Delete" }).GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
            await Expect(p.GetByText($"{name} is in the trash now.")).ToBeVisibleAsync();
        }

        await HeaderLink(p, "Trash").ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Trash", Level = 1 })).ToBeVisibleAsync();
        foreach (var name in deleted)
        {
            await Expect(p.GetByRole(AriaRole.Row).Filter(new() { HasText = name })).ToHaveCountAsync(1);
        }

        await SaveAsync(p, folder, "08-trash");

        // The users of the team, in the dark theme.
        await HeaderLink(p, "Users").ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Users", Level = 1 })).ToBeVisibleAsync();
        foreach (var user in DemoKiler.Users)
        {
            await Expect(p.GetByRole(AriaRole.Row).Filter(new() { HasText = user.Account })).ToHaveCountAsync(1);
        }

        var theme = p.GetByLabel("Theme");
        await theme.SelectOptionAsync(new SelectOptionValue { Label = "Dark" });
        await Expect(p.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        await theme.BlurAsync();
        await SaveAsync(p, folder, "10-users-dark");
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
