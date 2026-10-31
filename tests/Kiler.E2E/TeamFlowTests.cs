using Kiler.App.Tests;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Kiler.E2E;

/// <summary>
/// The flow of M4 in the published Kiler.exe and Playwright's Chromium: the wizard of a new Kiler, the sign-in, the admin
/// adds a user, and that user signs in with <c>--account</c> and changes the temporary password. The accounts are made up,
/// and both runs use <c>--account</c>, so the test does not depend on the Windows account that runs it.
/// </summary>
[Trait("Category", "E2E")]
public class TeamFlowTests(KilerBrowser browser)
{
    private const string Admin = "e2e.admin";
    private const string AdminName = "E2E Admin";
    private const string AdminPassword = "admin-password-1";
    private const string User = "e2e.user";
    private const string UserName = "E2E User";
    private const string UserPassword = "user-password-1";

    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task Wizard_SignIn_TheAdminAddsAUser_WhoSignsInWithAccount_AndChangesTheTemporaryPassword()
    {
        using var exe = new KilerExe();
        string temporary;

        using (var run = KilerBrowser.Start(exe, "--account", Admin))
        {
            await using var page = await browser.OpenAsync(run);
            var p = page.Page;

            // The wizard, with the levels that it offers: Guest, Team, Test and Prod.
            await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "New Kiler" })).ToBeVisibleAsync();
            await p.GetByLabel("Display name").FillAsync(AdminName);
            await p.GetByLabel("Password", new() { Exact = true }).FillAsync(AdminPassword);
            await p.GetByLabel("Password again").FillAsync(AdminPassword);
            await p.GetByRole(AriaRole.Button, new() { Name = "Make the new Kiler" }).ClickAsync();

            await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "The recovery key" })).ToBeVisibleAsync();
            await Expect(p.Locator(".secret-value")).Not.ToBeEmptyAsync();
            await p.GetByLabel("I have saved the recovery key outside Kiler").CheckAsync();
            await p.GetByRole(AriaRole.Button, new() { Name = "Continue to the sign-in" }).ClickAsync();

            await SignInAsync(p, Admin, AdminPassword);
            await Expect(p.GetByRole(AriaRole.Banner).GetByText(AdminName)).ToBeVisibleAsync();

            // The admin adds a user, who gets a temporary password.
            await p.GetByRole(AriaRole.Link, new() { Name = "Users" }).ClickAsync();
            await p.GetByRole(AriaRole.Button, new() { Name = "Add a user" }).ClickAsync();
            await p.GetByLabel("Windows user name").FillAsync(User);
            await p.GetByLabel("Display name").FillAsync(UserName);
            await p.GetByLabel("Level", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "2 · Test" });
            await p.GetByLabel("Editor: makes and changes records up to their level").CheckAsync();
            await p.GetByRole(AriaRole.Button, new() { Name = "Add the user" }).ClickAsync();

            await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "The temporary password" })).ToBeVisibleAsync();
            temporary = await p.Locator(".secret-value").InnerTextAsync();
            Assert.True(temporary.Length >= 8, "The temporary password is shorter than a password may be.");
            await p.GetByRole(AriaRole.Button, new() { Name = "I have it. Close" }).ClickAsync();
            await Expect(p.GetByRole(AriaRole.Row).Filter(new() { HasText = User })).ToContainTextAsync("Temporary password");

            await page.CloseAsync();
            await KilerBrowser.WaitForStopAsync(run);
        }

        // A Kiler that runs on the data folder would take the second start, and ignore its --account (M4.1).
        using (var run = KilerBrowser.Start(exe, "--account", User))
        {
            await using var page = await browser.OpenAsync(run);
            var p = page.Page;

            await SignInAsync(p, User, temporary);
            await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Choose your password" })).ToBeVisibleAsync();
            await p.GetByLabel("New password", new() { Exact = true }).FillAsync(UserPassword);
            await p.GetByLabel("New password again").FillAsync(UserPassword);
            await p.GetByRole(AriaRole.Button, new() { Name = "Change the password" }).ClickAsync();

            // The forced change goes on to the start page; the new password then unlocks the session that "Lock" ends.
            await Expect(p).ToHaveURLAsync(page.Root);
            var account = p.GetByRole(AriaRole.Banner);
            await Expect(account.GetByText(UserName)).ToBeVisibleAsync();
            await account.GetByRole(AriaRole.Button, new() { Name = "Lock" }).ClickAsync();
            await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Kiler is locked" })).ToBeVisibleAsync();
            await p.GetByLabel("Password", new() { Exact = true }).FillAsync(UserPassword);
            await p.GetByRole(AriaRole.Button, new() { Name = "Unlock" }).ClickAsync();
            await Expect(account.GetByText(UserName)).ToBeVisibleAsync();

            await page.CloseAsync();
            await KilerBrowser.WaitForStopAsync(run);
        }
    }

    /// <summary>The sign-in of the account that the run took from <c>--account</c>.</summary>
    private static async Task SignInAsync(IPage p, string account, string password)
    {
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync();
        await Expect(p.GetByText($"Windows account: {account}")).ToBeVisibleAsync();
        await p.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await p.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
    }
}
