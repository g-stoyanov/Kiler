using System.Globalization;
using System.Text.RegularExpressions;
using Kiler.App.Tests;
using Kiler.DemoData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Kiler.E2E;

/// <summary>
/// The demo data of the tests of the screens (M6.12): a copy of the published exe whose data folder holds the made-up Kiler
/// of <see cref="DemoKiler"/>, made by the code of the tool, and the parts of the screens that the tests share. Each test
/// makes a Kiler of its own, as a write of one test would change what another reads, and a second start on the same data
/// folder would go to the first run (M4.1). Guest mode is on in the demo, so a page opens at the main view of a guest.
/// </summary>
internal static class Demo
{
    /// <summary>A copy of the exe on a new data folder with the demo Kiler; its PBKDF2 takes a few seconds.</summary>
    public static KilerExe NewExe()
    {
        var exe = new KilerExe();
        try
        {
            var work = new TempFolder();
            try
            {
                DemoKiler.Make(exe.DataFolder, work.Path);
            }
            finally
            {
                DeleteWork(work);
            }

            return exe;
        }
        catch
        {
            exe.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Deletes the work folder of <see cref="DemoKiler.Make"/>. It is only a temporary folder, so an antivirus that still reads
    /// a file just written there neither fails the test nor hides the failure of the make.
    /// </summary>
    private static void DeleteWork(TempFolder work)
    {
        try
        {
            work.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Starts a run of <paramref name="exe"/> whose Windows account is that of <paramref name="user"/>.</summary>
    public static KilerRun Start(KilerExe exe, DemoUser user) => KilerBrowser.Start(exe, "--account", user.Account);

    /// <summary>Opens the page of <paramref name="run"/>, which shows the main view to a guest, and signs <paramref name="user"/> in from its header.</summary>
    public static async Task<KilerPage> OpenSignedInAsync(KilerBrowser browser, KilerRun run, DemoUser user)
    {
        var page = await browser.OpenAsync(run);
        try
        {
            var p = page.Page;
            await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Records" })).ToBeVisibleAsync();
            await SignInAsync(p, user, p.GetByRole(AriaRole.Banner).GetByRole(AriaRole.Link, new() { Name = "Sign in" }));
            return page;
        }
        catch
        {
            await page.DisposeAsync();
            throw;
        }
    }

    /// <summary>Clicks <paramref name="signIn"/>, a "Sign in" of the page, and signs <paramref name="user"/> in with the demo password.</summary>
    public static async Task SignInAsync(IPage p, DemoUser user, ILocator signIn)
    {
        await signIn.ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync();
        await Expect(p.GetByText($"Windows account: {user.Account}")).ToBeVisibleAsync();
        await p.GetByLabel("Password", new() { Exact = true }).FillAsync(user.Password);
        await p.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Banner).GetByText(user.DisplayName)).ToBeVisibleAsync();
    }

    /// <summary>The count of the main view, such as "31 records".</summary>
    public static ILocator Count(IPage p) => p.GetByRole(AriaRole.Main).GetByRole(AriaRole.Status);

    /// <summary>The count of the main view for <paramref name="count"/> records.</summary>
    public static string Records(int count) =>
        count == 1 ? "1 record" : string.Create(CultureInfo.InvariantCulture, $"{count} records");

    /// <summary>The records that the main view lists.</summary>
    public static ILocator List(IPage p) => p.Locator(".record-list");

    /// <summary>The link of a record of the list, by its whole name.</summary>
    public static ILocator RecordLink(IPage p, string name) =>
        List(p).GetByRole(AriaRole.Link, new() { Name = name, Exact = true });

    /// <summary>The stash tree of the main view.</summary>
    public static ILocator Tree(IPage p) => p.GetByRole(AriaRole.Navigation, new() { Name = "Stashes" });

    /// <summary>The heading of the record view, the record's name.</summary>
    public static ILocator Title(IPage p, string name) =>
        p.GetByRole(AriaRole.Heading, new() { Name = name, Exact = true, Level = 1 });

    /// <summary>The links of the stash path of the record view.</summary>
    public static ILocator PathLinks(IPage p) =>
        p.GetByRole(AriaRole.Navigation, new() { Name = "Stash path" }).GetByRole(AriaRole.Link);

    /// <summary>A field of the record view, by its whole label.</summary>
    public static ILocator Field(IPage p, string label) =>
        p.Locator(".record-field").Filter(new() { Has = p.Locator("dt", new() { HasTextRegex = new Regex($"^{Regex.Escape(label)}$") }) });

    /// <summary>The name of the record form.</summary>
    public static ILocator NameInput(IPage p) => p.GetByLabel("Name (required)", new() { Exact = true });

    /// <summary>Opens the record of the main view's list named <paramref name="name"/>.</summary>
    public static async Task OpenRecordAsync(IPage p, string name)
    {
        await RecordLink(p, name).ClickAsync();
        await Expect(Title(p, name)).ToBeVisibleAsync();
    }

    /// <summary>Goes back to the main view with the brand of the header.</summary>
    public static async Task ToMainViewAsync(IPage p)
    {
        await p.GetByRole(AriaRole.Banner).GetByRole(AriaRole.Link, new() { Name = "Kiler", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Records", Level = 1 })).ToBeVisibleAsync();
    }

    /// <summary>Saves the record form, which goes on to the record view of <paramref name="name"/>.</summary>
    public static async Task SaveAsync(IPage p, string name)
    {
        await p.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(Title(p, name)).ToBeVisibleAsync();
    }

    /// <summary>Chooses the template of a new record.</summary>
    public static Task ChooseTemplateAsync(IPage p, string name) =>
        p.GetByLabel("Template", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = name });

    /// <summary>The open position <paramref name="position"/> of the stash picker.</summary>
    public static ILocator StashInput(IPage p, int position) =>
        p.GetByRole(AriaRole.Combobox, new() { Name = $"Stash {position}", Exact = true });

    /// <summary>Types <paramref name="typed"/> at the open position <paramref name="position"/> of the stash picker and chooses the stash <paramref name="name"/>.</summary>
    public static async Task ChooseStashAsync(IPage p, int position, string typed, string name)
    {
        await StashInput(p, position).FillAsync(typed);
        await p.GetByRole(AriaRole.Option, new() { Name = name, Exact = true }).ClickAsync();
        await Expect(ClearStash(p, position, name)).ToBeVisibleAsync();
    }

    /// <summary>Makes the stash <paramref name="name"/> with "Create …" at the open position <paramref name="position"/>, which then chooses it.</summary>
    public static async Task CreateStashAsync(IPage p, int position, string name)
    {
        await StashInput(p, position).FillAsync(name);
        await p.GetByRole(AriaRole.Option, new() { Name = $"Create “{name}”", Exact = true }).ClickAsync();
        await Expect(ClearStash(p, position, name)).ToBeVisibleAsync();
    }

    private static ILocator ClearStash(IPage p, int position, string name) =>
        p.GetByRole(AriaRole.Button, new() { Name = $"Clear stash {position}, {name}, and those after it", Exact = true });
}
