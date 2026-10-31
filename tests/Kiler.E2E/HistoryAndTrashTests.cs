using Kiler.DemoData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Kiler.E2E;

/// <summary>
/// The history and the trash (M6.14) on the demo data, as the demo admin: an old version restored after an edit, and a
/// deleted record restored from the trash. The local copy of a new run takes no history at its first load (host.md, the
/// M5.11 note), so the old version is the one that the edit of the test replaced.
/// </summary>
[Trait("Category", "E2E")]
public class HistoryAndTrashTests(KilerBrowser browser)
{
    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task AnOldVersionIsRestored_AndADeletedRecordComesBackFromTheTrash()
    {
        const string RecordName = "Holiday calendar";
        const string Renamed = "Holiday calendar, renamed";
        var admin = DemoKiler.Admin;
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, admin);
        await using var page = await Demo.OpenSignedInAsync(browser, run, admin);
        var p = page.Page;

        await Demo.OpenRecordAsync(p, RecordName);
        await p.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }).ClickAsync();
        await Demo.NameInput(p).FillAsync(Renamed);
        await Demo.SaveAsync(p, Renamed);
        await p.GetByRole(AriaRole.Link, new() { Name = "History", Exact = true }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "History", Level = 1 })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Cell, new() { Name = "2 (current)", Exact = true })).ToBeVisibleAsync();
        await p.GetByRole(AriaRole.Button, new() { Name = "Restore", Exact = true }).ClickAsync();
        await Expect(Demo.Title(p, RecordName)).ToBeVisibleAsync();

        // "Delete" asks first; the record then waits in the trash, whose "Restore" brings it back.
        await p.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await p.GetByRole(AriaRole.Group, new() { Name = "Delete" }).GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(p.GetByText($"{RecordName} is in the trash now.")).ToBeVisibleAsync();
        await p.GetByRole(AriaRole.Link, new() { Name = "To the trash", Exact = true }).ClickAsync();
        var row = p.GetByRole(AriaRole.Row).Filter(new() { HasText = RecordName });
        await row.GetByRole(AriaRole.Button, new() { Name = "Restore", Exact = true }).ClickAsync();
        await Expect(Demo.Title(p, RecordName)).ToBeVisibleAsync();

        await Demo.ToMainViewAsync(p);
        await Expect(Demo.Count(p)).ToHaveTextAsync(Demo.Records(DemoRecords.Set.Count));
        await Expect(Demo.RecordLink(p, RecordName)).ToBeVisibleAsync();

        await page.CloseAsync();
    }
}
