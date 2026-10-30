using System.Net;
using Kiler.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace Kiler.App.Tests;

/// <summary>The serving of the UI files, with made-up files (<see cref="MadeUpUi"/>), against the real Kestrel.</summary>
public class UiFilesTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // The root and the addresses of the UI's own routing get the page, so a reload there works. /apiary is not /api.
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/records")]
    [InlineData("/records/12/edit")]
    [InlineData("/records?view=tree")]
    [InlineData("/apiary")]
    [InlineData("/assets")]
    public async Task Get_PageAddress_ReturnsThePageWithItsHeaders(string path)
    {
        using var ui = new MadeUpUi();
        await using var server = await KilerServer.StartAsync(ui.Files);
        using var client = server.CreateSignedInClient();

        using var response = await client.GetAsync(path, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(MadeUpUi.Page, await response.Content.ReadAsStringAsync(Cancel));
        Assert.Equal("no-store", Header(response, "Cache-Control"));
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Head_PageAddress_ReturnsThePageHeadersWithoutABody()
    {
        using var ui = new MadeUpUi();
        await using var server = await KilerServer.StartAsync(ui.Files);
        using var client = server.CreateSignedInClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, "/records/12");

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Cancel));
        Assert.Equal("no-store", Header(response, "Cache-Control"));
    }

    // The browser refuses a worker script that is not JavaScript.
    [Theory]
    [InlineData(MadeUpUi.Script, "text/javascript")]
    [InlineData(MadeUpUi.Worker, "text/javascript")]
    [InlineData(MadeUpUi.Style, "text/css")]
    public async Task Get_HashedFile_ReturnsItWithItsTypeCachedForAYear(string path, string type)
    {
        using var ui = new MadeUpUi();
        await using var server = await KilerServer.StartAsync(ui.Files);
        using var client = server.CreateSignedInClient();

        using var response = await client.GetAsync(path, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("private, max-age=31536000, immutable", Header(response, "Cache-Control"));
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Get_FileWithoutAHash_ReturnsItCheckedEachTime()
    {
        using var ui = new MadeUpUi();
        await using var server = await KilerServer.StartAsync(ui.Files);
        using var client = server.CreateSignedInClient();

        using var response = await client.GetAsync(MadeUpUi.Icon, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", Header(response, "Cache-Control"));
        AssertSecurityHeaders(response);
    }

    // A name with a dot is a file, so a missing one is 404 and not the page.
    [Theory]
    [InlineData("/assets/index-Missing1.js")]
    [InlineData("/missing.png")]
    [InlineData("/records/report.pdf")]
    public async Task Get_MissingFile_Returns404(string path)
    {
        using var ui = new MadeUpUi();
        await using var server = await KilerServer.StartAsync(ui.Files);
        using var client = server.CreateSignedInClient();

        using var response = await client.GetAsync(path, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The API never falls back to the page, so a call to a missing endpoint fails as such.
    [Theory]
    [InlineData("GET", "/api")]
    [InlineData("GET", "/api/")]
    [InlineData("GET", "/api/unknown")]
    [InlineData("GET", "/API/Unknown")]
    [InlineData("GET", "/api/records/12/unknown")]
    [InlineData("HEAD", "/api/unknown")]
    [InlineData("POST", "/api/unknown")]
    public async Task Api_WithoutEndpoint_Returns404(string method, string path)
    {
        using var ui = new MadeUpUi();
        await using var server = await KilerServer.StartAsync(ui.Files);
        using var client = server.CreateSignedInClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    // Only a GET or a HEAD gets the page.
    [Theory]
    [InlineData("POST", "/")]
    [InlineData("POST", "/records")]
    [InlineData("PUT", "/index.html")]
    [InlineData("DELETE", MadeUpUi.Script)]
    public async Task OtherMethod_OnAPageOrAFile_Returns404(string method, string path)
    {
        using var ui = new MadeUpUi();
        await using var server = await KilerServer.StartAsync(ui.Files);
        using var client = server.CreateSignedInClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // An endpoint outside /api at a page address runs, and sees the address it was called with, not the page's.
    [Fact]
    public async Task Endpoint_WinsOverThePage()
    {
        using var ui = new MadeUpUi();
        var app = KilerHost.Build(new StartOptions(), ui.Files);
        app.MapGet("/records/{id}", (HttpContext context) => context.Request.Path.Value).Needs(Need.Nothing);
        await using var server = await KilerServer.StartAsync(app);
        using var client = server.CreateSignedInClient();

        using var response = await client.GetAsync("/records/12", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/records/12", await response.Content.ReadAsStringAsync(Cancel));
    }

    // A Debug build embeds no UI, and then there is nothing to serve.
    [Fact]
    public async Task Get_WithoutUiFiles_Returns404()
    {
        await using var server = await KilerServer.StartAsync(new NullFileProvider());
        using var client = server.CreateSignedInClient();

        using var response = await client.GetAsync("/", Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The header as the server sent it. Parsed, HttpClient would write Cache-Control in an order of its own.</summary>
    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.NonValidated.TryGetValues(name, out var values) ? string.Join(", ", values) : "";

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal(
            "default-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'",
            Header(response, "Content-Security-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("same-origin", Header(response, "Referrer-Policy"));
    }
}
