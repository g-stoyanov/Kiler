using System.Text.RegularExpressions;
using Kiler.DemoData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Kiler.E2E;

/// <summary>
/// The audit screen (M6.19) on the demo data: a password of Prod that the admin shows is a row of the day, whose link
/// opens the record, and the day before has none; a user who is not an admin has no link, and <c>/audit</c> takes them to
/// the main view. A view just before local midnight and the screen just after it would put the row on the day before.
/// </summary>
[Trait("Category", "E2E")]
public class AuditTests(KilerBrowser browser)
{
    private const string RecordName = "Billing prod database";

    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task TheAdminShowsAProdPassword_AndSeesItsRowInTheAudit()
    {
        var admin = DemoKiler.Admin;
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, admin);
        await using var page = await Demo.OpenSignedInAsync(browser, run, admin);
        var p = page.Page;

        await Demo.OpenRecordAsync(p, RecordName);
        var field = Demo.Field(p, "Password");
        await field.GetByRole(AriaRole.Button, new() { Name = "Show Password", Exact = true }).ClickAsync();
        await Expect(field.Locator(".secret-shown")).ToBeVisibleAsync();

        await p.GetByRole(AriaRole.Banner).GetByRole(AriaRole.Link, new() { Name = "Audit", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Audit", Level = 1 })).ToBeVisibleAsync();
        var rows = p.GetByRole(AriaRole.Main).GetByRole(AriaRole.Row).Filter(new() { HasText = RecordName });
        await Expect(rows).ToHaveCountAsync(1);
        var cells = rows.GetByRole(AriaRole.Cell);
        await Expect(cells.Nth(0)).ToHaveTextAsync(new Regex(@"^\d{2}:\d{2}:\d{2}$"));
        await Expect(cells.Nth(1)).ToHaveTextAsync(admin.Account);
        await Expect(cells.Nth(3)).ToHaveTextAsync("password");
        await Expect(cells.Nth(4)).ToHaveTextAsync("Show");

        var day = p.GetByLabel("Day", new() { Exact = true });
        var today = await day.InputValueAsync();
        await p.GetByRole(AriaRole.Button, new() { Name = "Previous", Exact = true }).ClickAsync();
        await Expect(day).Not.ToHaveValueAsync(today);
        await Expect(p.GetByText("No one showed or copied a password of the top level on this day.")).ToBeVisibleAsync();
        await p.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
        await Expect(day).ToHaveValueAsync(today);
        await Expect(rows).ToHaveCountAsync(1);

        await rows.GetByRole(AriaRole.Link, new() { Name = RecordName, Exact = true }).ClickAsync();
        await Expect(Demo.Title(p, RecordName)).ToBeVisibleAsync();

        await page.CloseAsync();
    }

    /// <summary>
    /// The address goes into a second page of the context, as <c>ConflictTests</c> does, so the first page holds the stream
    /// and Kiler runs on (host.md, "E2E tests").
    /// </summary>
    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task AReaderHasNoAuditLink_AndTheAddressTakesThemToTheMainView()
    {
        var reader = DemoKiler.Users.Single(user => !user.CanEdit);
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, reader);
        await using var page = await Demo.OpenSignedInAsync(browser, run, reader);
        var p = page.Page;

        var banner = p.GetByRole(AriaRole.Banner);
        await Expect(banner.GetByRole(AriaRole.Link, new() { Name = "Trash", Exact = true })).ToBeVisibleAsync();
        await Expect(banner.GetByRole(AriaRole.Link, new() { Name = "Audit", Exact = true })).ToHaveCountAsync(0);

        var second = await p.Context.NewPageAsync();
        await second.GotoAsync($"{page.Root}audit");
        await Expect(second.GetByRole(AriaRole.Heading, new() { Name = "Records", Level = 1 })).ToBeVisibleAsync();
        Assert.Equal(page.Root, second.Url);
        await Expect(second.GetByRole(AriaRole.Heading, new() { Name = "Audit" })).ToHaveCountAsync(0);

        await page.CloseAsync();
    }
}
