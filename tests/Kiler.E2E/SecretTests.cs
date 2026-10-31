using Kiler.DemoData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Kiler.E2E;

/// <summary>
/// A password of the demo data in the record view (M6.5): its padlock for a guest, whose "Sign in" comes back to the
/// record, and "Show", "Hide" and "Copy" for the admin, the copy read back from the clipboard of Chromium; and how long a
/// shown password stays while other pages write (M6.16). The password is made up and public; the <c>Assert</c> that
/// compares it prints no value (SKILL.md, "Tests"), though a failed <c>Expect</c> of the field prints its text, the shown
/// value too.
/// </summary>
[Trait("Category", "E2E")]
public class SecretTests(KilerBrowser browser)
{
    private const string RecordName = "Billing prod database";

    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task AGuestSeesThePadlock_AndTheAdminShows_Hides_AndCopiesThePassword()
    {
        var admin = DemoKiler.Admin;
        var password = Password();
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, admin);
        await using var page = await browser.OpenAsync(run);
        var p = page.Page;
        await p.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);

        // The password of prod is at the top level, so a guest gets its padlock, with "Sign in" beside it.
        await Demo.OpenRecordAsync(p, RecordName);
        var field = Demo.Field(p, "Password");
        await Expect(field).ToContainTextAsync("requires level Prod");
        await Demo.SignInAsync(p, admin, field.GetByRole(AriaRole.Link, new() { Name = "Sign in", Exact = true }));
        await Expect(Demo.Title(p, RecordName)).ToBeVisibleAsync();

        await field.GetByRole(AriaRole.Button, new() { Name = "Show Password", Exact = true }).ClickAsync();
        var shown = field.Locator(".secret-shown");
        await Expect(shown).ToBeVisibleAsync();
        Assert.True(await shown.InnerTextAsync() == password, "The value shown is not the password of the record.");
        await Expect(field).ToContainTextAsync("It hides again after 30 seconds.");
        await field.GetByRole(AriaRole.Button, new() { Name = "Hide Password", Exact = true }).ClickAsync();
        await Expect(shown).ToHaveCountAsync(0);
        await Expect(field).ToContainTextAsync("••••");

        var copy = field.GetByRole(AriaRole.Button, new() { Name = "Copy Password", Exact = true });
        await copy.ClickAsync();
        await Expect(copy).ToHaveTextAsync("Copied");
        var copied = await p.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        Assert.True(copied == password, "The clipboard does not hold the password of the record.");

        await page.CloseAsync();
    }

    /// <summary>
    /// A second page of the context shares the session and the stream, as <c>ConflictTests</c> does. Its save of another
    /// record gives <c>changed</c>, at which the first page reads its record again, in the same version, so the password
    /// stays; its save of the record of the first page gives a new version, which hides it. The second page holds the
    /// form of the other record before the password shows, so the rest comes well within the 30 seconds of a shown value.
    /// </summary>
    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task AShownPassword_StaysWhenAnotherRecordChanges_AndHidesWhenItsRecordChanges()
    {
        const string OtherName = "Welcome to the team";
        const string OtherSaved = "Welcome to the team, saved by the second page";
        const string Saved = "Billing prod database, saved by the second page";
        var admin = DemoKiler.Admin;
        var password = Password();
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, admin);
        await using var page = await Demo.OpenSignedInAsync(browser, run, admin);
        var first = page.Page;
        await Demo.OpenRecordAsync(first, RecordName);
        var address = first.Url;
        var id = new Uri(address).AbsolutePath["/records/".Length..];

        var second = await first.Context.NewPageAsync();
        await second.GotoAsync(page.Root);
        await Demo.OpenRecordAsync(second, OtherName);
        await second.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }).ClickAsync();
        await Demo.NameInput(second).FillAsync(OtherSaved);

        var field = Demo.Field(first, "Password");
        await field.GetByRole(AriaRole.Button, new() { Name = "Show Password", Exact = true }).ClickAsync();
        var shown = field.Locator(".secret-shown");
        await Expect(shown).ToBeVisibleAsync();

        // Before M6.16 the read hid the value as it started, so it is gone by the time the request goes out.
        var read = first.WaitForRequestAsync(request => IsReadOf(request, id));
        await Demo.SaveAsync(second, OtherSaved);
        var response = await (await read).ResponseAsync();
        Assert.NotNull(response);
        await response.FinishedAsync();
        await Expect(shown).ToBeVisibleAsync();
        Assert.True(await shown.InnerTextAsync() == password, "The value shown is not the password of the record.");

        await second.GotoAsync($"{address}/edit");
        await Demo.NameInput(second).FillAsync(Saved);
        await Demo.SaveAsync(second, Saved);
        await Expect(Demo.Title(first, Saved)).ToBeVisibleAsync();
        await Expect(shown).ToHaveCountAsync(0);
        await Expect(field).ToContainTextAsync("••••");

        await page.CloseAsync();
    }

    /// <summary>The made-up password of the record.</summary>
    private static string? Password() =>
        DemoRecords.Set.Single(record => record.Name == RecordName).Fields["password"].Text;

    /// <summary>A read of the record <paramref name="id"/>, which the record view makes.</summary>
    private static bool IsReadOf(IRequest request, string id) =>
        request.Method == "GET" && new Uri(request.Url).AbsolutePath == $"/api/records/{id}";
}
