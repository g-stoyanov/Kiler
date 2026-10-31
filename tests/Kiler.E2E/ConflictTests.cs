using System.Text.RegularExpressions;
using Kiler.DemoData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Kiler.E2E;

/// <summary>
/// The conflict of two pages (M6.8, M6.20) and the changes of other pages (M6.11), on the demo data. Two pages of one
/// context share the cookie, so the session, and the stream of the shared worker: the second page saves an edit of a
/// record while the first holds its form, whose save then gets the conflict with the two versions side by side, and
/// saves over it with "Keep mine". The second page shows that without a reload, by a read that carries the mark of no
/// activity until the user's next input there. A second conflict then ends with "Take theirs".
/// </summary>
[Trait("Category", "E2E")]
public partial class ConflictTests(KilerBrowser browser)
{
    private const string ActivityHeader = "kiler-activity";

    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task TheSaveOfAnOlderForm_ShowsBothVersions_KeepMineShowsInTheOtherPageWithoutAReload_TakeTheirsTakesTheirs()
    {
        const string RecordName = "Welcome to the team";
        const string FromSecond = "Welcome to the team, saved by the second page";
        const string FromFirst = "Welcome to the team, saved by the first page";
        const string SecondAgain = "Welcome to the team, saved by the second page again";
        var admin = DemoKiler.Admin;
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, admin);
        await using var page = await Demo.OpenSignedInAsync(browser, run, admin);
        var first = page.Page;
        await Demo.OpenRecordAsync(first, RecordName);
        await first.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }).ClickAsync();
        await Expect(first.GetByRole(AriaRole.Heading, new() { Name = "Edit the record", Level = 1 })).ToBeVisibleAsync();
        var id = EditAddress().Match(new Uri(first.Url).AbsolutePath) is { Success: true } match
            ? match.Groups["id"].Value
            : throw new InvalidOperationException($"The edit is not at /records/<id>/edit: {first.Url}");

        var second = await first.Context.NewPageAsync();
        await second.GotoAsync(first.Url);
        await Expect(second.GetByRole(AriaRole.Heading, new() { Name = "Edit the record", Level = 1 })).ToBeVisibleAsync();
        await Demo.NameInput(second).FillAsync(FromSecond);
        await Demo.SaveAsync(second, FromSecond);

        // The first page still holds the version it read, so its save gets the conflict, and its draft stays.
        await Demo.NameInput(first).FillAsync(FromFirst);
        await first.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        var conflict = first.Locator(".form-conflict");
        var keepMine = conflict.GetByRole(AriaRole.Button, new() { Name = "Keep mine", Exact = true });
        await Expect(conflict).ToContainTextAsync($"{admin.DisplayName} changed the record at");
        await Expect(Demo.NameInput(first)).ToHaveValueAsync(FromFirst);
        await Expect(keepMine).ToBeFocusedAsync();

        // The two versions side by side: the name differs, and is marked in both columns; the tags do not.
        var mine = conflict.GetByRole(AriaRole.Region, new() { Name = "Your edit", Exact = true });
        var theirs = conflict.GetByRole(AriaRole.Region, new() { Name = $"Saved by {admin.DisplayName} at" });
        await Expect(Row(mine, "Name")).ToContainTextAsync(FromFirst);
        await Expect(Row(theirs, "Name")).ToContainTextAsync(FromSecond);
        await Expect(Row(mine, "Name")).ToContainTextAsync("Differs");
        await Expect(Row(theirs, "Name")).ToContainTextAsync("Differs");
        await Expect(Row(mine, "Tags")).ToContainTextAsync("onboarding");
        await Expect(Row(theirs, "Tags")).ToContainTextAsync("onboarding");
        await Expect(Row(theirs, "Tags")).Not.ToContainTextAsync("Differs");

        // The second page has had no input since its save, so its read at the changed event is no activity.
        var read = second.WaitForRequestAsync(request => IsReadOf(request, id));
        await keepMine.ClickAsync();
        await Expect(Demo.Title(first, FromFirst)).ToBeVisibleAsync();
        await Expect(Demo.Title(second, FromFirst)).ToBeVisibleAsync();
        Assert.Equal("none", await (await read).HeaderValueAsync(ActivityHeader));

        // A click in the page ends the mark, so the read that follows it counts as activity again. The mark is decided as
        // the read is made, and the load after the two writes gives one more changed within 10 s (docs/api.md), which can
        // come between a click and its read: so a marked read goes back with "Cancel" and clicks again. Without the end of
        // the mark at an input, every try is marked.
        var unmarked = false;
        for (var tries = 0; tries < 3 && !unmarked; tries++)
        {
            var edit = await second.RunAndWaitForRequestAsync(
                () => second.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }).ClickAsync(),
                request => IsReadOf(request, id));
            await Expect(Demo.NameInput(second)).ToHaveValueAsync(FromFirst);
            unmarked = await edit.HeaderValueAsync(ActivityHeader) is null;
            if (!unmarked)
            {
                await second.GetByRole(AriaRole.Link, new() { Name = "Cancel", Exact = true }).ClickAsync();
                await Expect(Demo.Title(second, FromFirst)).ToBeVisibleAsync();
            }
        }

        Assert.True(unmarked, "Each read after a click in the page carried the mark of no activity.");

        // The second page holds its form of the version that "Keep mine" saved. The first page edits that version too,
        // and after the second page saves again, "Take theirs" puts the version of the second page into the form.
        await first.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }).ClickAsync();
        await Expect(Demo.NameInput(first)).ToHaveValueAsync(FromFirst);
        await Demo.NameInput(second).FillAsync(SecondAgain);
        await Demo.SaveAsync(second, SecondAgain);
        await Demo.NameInput(first).FillAsync($"{FromFirst} again");
        await first.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(Row(theirs, "Name")).ToContainTextAsync(SecondAgain);
        await conflict.GetByRole(AriaRole.Button, new() { Name = "Take theirs", Exact = true }).ClickAsync();
        await Expect(conflict).ToHaveCountAsync(0);
        await Expect(Demo.NameInput(first)).ToHaveValueAsync(SecondAgain);

        await page.CloseAsync();
    }

    /// <summary>A read of the record <paramref name="id"/>, which the record view and the form make.</summary>
    private static bool IsReadOf(IRequest request, string id) =>
        request.Method == "GET" && new Uri(request.Url).AbsolutePath == $"/api/records/{id}";

    /// <summary>
    /// The row of a column of the conflict whose label is <paramref name="label"/>: its term, which also holds the mark
    /// "Differs", and its value.
    /// </summary>
    private static ILocator Row(ILocator column, string label) =>
        column.GetByRole(AriaRole.Term).Filter(new() { HasTextRegex = new Regex($"^{Regex.Escape(label)}( |$)") }).Locator("xpath=..");

    [GeneratedRegex("^/records/(?<id>[^/]+)/edit$")]
    private static partial Regex EditAddress();
}
