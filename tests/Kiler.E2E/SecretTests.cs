using Kiler.DemoData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Kiler.E2E;

/// <summary>
/// A password of the demo data in the record view (M6.5): its padlock for a guest, whose "Sign in" comes back to the
/// record, and "Show", "Hide" and "Copy" for the admin, the copy read back from the clipboard of Chromium. The run makes
/// no write, so no <c>changed</c> event hides the shown value (the M6.11 entry under "Left for after the exam"). The
/// password is made up and public; the <c>Assert</c> that compares it prints no value (SKILL.md, "Tests"), though a failed
/// <c>Expect</c> of the field prints its text, the shown value too.
/// </summary>
[Trait("Category", "E2E")]
public class SecretTests(KilerBrowser browser)
{
    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task AGuestSeesThePadlock_AndTheAdminShows_Hides_AndCopiesThePassword()
    {
        var admin = DemoKiler.Admin;
        var record = DemoRecords.Set.Single(record => record.Name == "Billing prod database");
        var password = record.Fields["password"].Text;
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, admin);
        await using var page = await browser.OpenAsync(run);
        var p = page.Page;
        await p.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);

        // The password of prod is at the top level, so a guest gets its padlock, with "Sign in" beside it.
        await Demo.OpenRecordAsync(p, record.Name);
        var field = Demo.Field(p, "Password");
        await Expect(field).ToContainTextAsync("requires level Prod");
        await Demo.SignInAsync(p, admin, field.GetByRole(AriaRole.Link, new() { Name = "Sign in", Exact = true }));
        await Expect(Demo.Title(p, record.Name)).ToBeVisibleAsync();

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
}
