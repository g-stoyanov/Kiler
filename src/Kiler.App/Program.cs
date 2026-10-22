using System.Net.Sockets;
using Kiler.App;

if (!StartOptions.TryParse(args, out var options, out var error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(StartOptions.Usage);
    return 2;
}

switch (options.Mode)
{
    case StartMode.Help:
        Console.WriteLine(StartOptions.Usage);
        return 0;
    case StartMode.Diag:
        // The --diag mode comes in M1.11.
        Console.Error.WriteLine("--diag is not available yet.");
        return 1;
}

await using var app = KilerHost.Build(options);
try
{
    await app.StartAsync();
}
// A port in use is an IOException. A port that Windows reserves (for example for Hyper-V) is a SocketException.
catch (Exception ex) when (options.Port is not null && ex is IOException or SocketException)
{
    Console.Error.WriteLine($"Kiler cannot listen on port {options.Port}: {ex.Message}");
    return 1;
}

var address = new Uri(app.Urls.Single());
Console.WriteLine($"Kiler is listening on {address.GetLeftPart(UriPartial.Authority)}. Ctrl+C stops it.");

// The address with the token lets anyone in, so it is printed only when no browser gets it (M1.5).
if (options.NoBrowser)
{
    Console.WriteLine($"Open: {AccessGuard.EntryAddress(address, app.Services.GetRequiredService<AccessToken>())}");
}

await app.WaitForShutdownAsync();
return 0;
