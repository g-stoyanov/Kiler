using System.Text.RegularExpressions;
using Kiler.DemoData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Kiler.E2E;

/// <summary>
/// The main view on the demo data (M6.3, M6.4): the search, the filters and the stash tree, as the demo reader, who has
/// no "+". The counts come from the records of the demo (<see cref="DemoRecords.Set"/>), all of which the list shows,
/// as a record has no level of its own until the private records.
/// </summary>
[Trait("Category", "E2E")]
public class BrowseTests(KilerBrowser browser)
{
    [Fact(Skip = KilerBrowser.NoExe, SkipUnless = nameof(KilerBrowser.HasExe), SkipType = typeof(KilerBrowser))]
    public async Task TheSearch_TheFilters_AndTheStashTree_NarrowTheList()
    {
        var reader = DemoKiler.Users.Single(user => !user.CanEdit);
        var set = DemoRecords.Set;
        using var exe = Demo.NewExe();
        using var run = Demo.Start(exe, reader);
        await using var page = await Demo.OpenSignedInAsync(browser, run, reader);
        var p = page.Page;
        var count = Demo.Count(p);
        await Expect(count).ToHaveTextAsync(Demo.Records(set.Count));

        // The search goes into the address, and the list keeps only what it finds.
        var search = p.GetByRole(AriaRole.Searchbox, new() { Name = "Search" });
        await search.FillAsync("library");
        await Expect(p).ToHaveURLAsync(new Regex(@"[?&]q=library(&|$)"));
        await Expect(Demo.RecordLink(p, "Fitness database")).ToHaveCountAsync(0);
        await Expect(Demo.RecordLink(p, "Monthly report to the library")).ToBeVisibleAsync();
        await search.FillAsync("");
        await Expect(count).ToHaveTextAsync(Demo.Records(set.Count));

        // A type, then a tag among its records; "Clear all" ends both.
        var filters = p.GetByRole(AriaRole.Region, new() { Name = "Filters" });
        var queries = set.Where(record => record.Template == "sql").ToList();
        var type = filters.GetByRole(AriaRole.Link, new() { Name = $"SQL query ({queries.Count})", Exact = true });
        await type.ClickAsync();
        await Expect(count).ToHaveTextAsync(Demo.Records(queries.Count));
        await Expect(type).ToHaveAttributeAsync("aria-current", "true");
        await filters.GetByRole(AriaRole.Link, new() { NameRegex = new Regex(@"^reporting \(\d+\)$") }).ClickAsync();
        await Expect(count).ToHaveTextAsync(Demo.Records(queries.Count(record => record.Tags.Contains("reporting"))));
        await Expect(p).ToHaveURLAsync(new Regex(@"[?&]tag=reporting(&|$)"));
        await filters.GetByRole(AriaRole.Link, new() { Name = "Clear all", Exact = true }).ClickAsync();
        await Expect(count).ToHaveTextAsync(Demo.Records(set.Count));

        // A field with a filter, the environment of a database, is a group of its own with its "Clear".
        var prod = set.Count(record => record.Fields.TryGetValue("env", out var env) && env.Text == "prod");
        var environment = filters.Locator(".filter-group").Filter(new() { Has = p.GetByRole(AriaRole.Heading, new() { Name = "Environment", Exact = true }) });
        await environment.GetByRole(AriaRole.Link, new() { Name = $"prod ({prod})", Exact = true }).ClickAsync();
        await Expect(count).ToHaveTextAsync(Demo.Records(prod));
        await Expect(Demo.RecordLink(p, "Photo blog hosting")).ToBeVisibleAsync();
        await environment.GetByRole(AriaRole.Link, new() { Name = "Clear Environment", Exact = true }).ClickAsync();
        await Expect(count).ToHaveTextAsync(Demo.Records(set.Count));

        // The tree starts collapsed to level 1, with each node's records and those under it; a reader gets no "+".
        var tree = Demo.Tree(p);
        await Expect(tree.GetByRole(AriaRole.Link, new() { NameRegex = new Regex(@"^New record") })).ToHaveCountAsync(0);
        await Expect(tree.GetByRole(AriaRole.Link, new() { NameRegex = new Regex(@"^Reports \(") })).ToHaveCountAsync(0);
        var library = set.Where(record => record.Path[0] == "Library app").ToList();
        var reports = library.Where(record => record.Path is [_, "Reports", ..]).ToList();
        await Expect(tree.GetByRole(AriaRole.Link, new() { Name = $"Library app ({library.Count})", Exact = true })).ToBeVisibleAsync();
        await tree.GetByRole(AriaRole.Button, new() { Name = "Open Library app", Exact = true }).ClickAsync();
        var reportsNode = tree.GetByRole(AriaRole.Link, new() { Name = $"Reports ({reports.Count})", Exact = true });
        await reportsNode.ClickAsync();
        await Expect(count).ToHaveTextAsync(Demo.Records(reports.Count));
        await Expect(reportsNode).ToHaveAttributeAsync("aria-current", "true");
        await Expect(p).ToHaveURLAsync(new Regex(@"[?&]stash="));

        // The node that the user opened stays open when the main view comes back without a node chosen, from what this
        // run of Kiler keeps; a chosen node would open the nodes above it anyway.
        await Demo.OpenRecordAsync(p, "Overdue loans by branch");
        await Expect(Demo.PathLinks(p)).ToHaveTextAsync(["Library app", "Reports"]);
        await Demo.ToMainViewAsync(p);
        await Expect(count).ToHaveTextAsync(Demo.Records(set.Count));
        var close = tree.GetByRole(AriaRole.Button, new() { Name = "Close Library app", Exact = true });
        await Expect(close).ToHaveAttributeAsync("aria-expanded", "true");

        // The stash path of a record leads back to its nodes.
        await Demo.OpenRecordAsync(p, "Overdue loans by branch");
        await Demo.PathLinks(p).Filter(new() { HasText = "Library app" }).ClickAsync();
        await Expect(count).ToHaveTextAsync(Demo.Records(library.Count));
        await tree.GetByRole(AriaRole.Link, new() { Name = $"All records ({set.Count})", Exact = true }).ClickAsync();
        await Expect(count).ToHaveTextAsync(Demo.Records(set.Count));

        // A node four stashes deep, once the nodes above it are open. Its name is unique among the nodes then shown:
        // Library app is closed, and "Photo blog" has "Live" under it, not "Sandbox".
        await close.ClickAsync();
        await Expect(tree.GetByRole(AriaRole.Button, new() { Name = "Open Library app", Exact = true })).ToHaveAttributeAsync("aria-expanded", "false");
        foreach (var node in new[] { "Photo blog", "Integrations", "Payments" })
        {
            await tree.GetByRole(AriaRole.Button, new() { Name = $"Open {node}", Exact = true }).ClickAsync();
        }

        var deep = set.Where(record => record.Path is ["Photo blog", "Integrations", "Payments", "Sandbox"]).ToList();
        await tree.GetByRole(AriaRole.Link, new() { Name = $"Sandbox ({deep.Count})", Exact = true }).ClickAsync();
        await Expect(count).ToHaveTextAsync(Demo.Records(deep.Count));
        await Expect(Demo.RecordLink(p, deep[0].Name)).ToBeVisibleAsync();

        await page.CloseAsync();
    }
}
