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
        // It measures a folder where it is started, with no server, and copies nothing.
        return await DiagMode.RunAsync(options);
}

var place = InstancePlace.ForCurrentUser(options.DataFolder);
var instanceFile = new InstanceFile(place.FilePath);
// Held until the end of Main, after the server has stopped. A start that hands over to the local copy lets it go first.
using var instanceLock = new InstanceMutex(place.MutexName);
var browser = new DefaultBrowser();

StartDecision decision;
try
{
    using var health = new HealthCheck(place.CookieName);
    decision = await new SingleInstance(instanceLock, instanceFile, health, TimeProvider.System).DecideAsync(CancellationToken.None);
}
// The mutex cannot be made or opened, for example because another user made one of the same name first.
catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
{
    Console.Error.WriteLine($"Kiler cannot take its instance lock: {ex.Message}");
    return 1;
}

switch (decision)
{
    case StartDecision.OpenRunning(var running):
        Console.WriteLine($"Kiler is already running on {running.Server.GetLeftPart(UriPartial.Authority)}.");
        if (options.Port is { } port && port != running.Port)
        {
            Console.WriteLine($"--port {port} is ignored.");
        }

        if (options.FirstWait is not null)
        {
            Console.WriteLine("--first-wait is ignored.");
        }

        if (options.IdleWait is not null)
        {
            Console.WriteLine("--idle-wait is ignored.");
        }

        Browser.OpenOrPrint(browser, running.EntryAddress, options.NoBrowser, Console.Out, Console.Error);
        return 0;
    case StartDecision.NoAnswer:
        Console.Error.WriteLine("Kiler is already running, but it does not answer. End Kiler.exe in Task Manager and start it again.");
        return 1;
    case StartDecision.StaleFileStays:
        Console.Error.WriteLine($"Kiler cannot delete the file of an earlier run: {instanceFile.Path}. Delete it and start Kiler again.");
        return 1;
}

// Started from a shared folder, Kiler runs from its copy in the profile. The single instance comes first, so a Kiler that
// runs already only gets the browser. The copy takes the lock itself.
var localCopy = new LocalCopy(CopyPlace.ForCurrentUser(), ShareFile.ForCurrentUser(), new CopyFiles(), new ShellShortcuts(), new ProcessStarter());
var copyDecision = localCopy.Decide(Environment.ProcessPath);
if (copyDecision is not LocalCopyDecision.RunHere)
{
    instanceLock.Dispose();
    return localCopy.Start(copyDecision, options, Console.Out, Console.Error);
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
Console.WriteLine($"Kiler is listening on {address.GetLeftPart(UriPartial.Authority)}. It stops when its last page closes, or with Ctrl+C.");

var self = new RunningInstance(address.Port, app.Services.GetRequiredService<AccessToken>());
try
{
    instanceFile.WriteUntilStopping(self, app.Lifetime.ApplicationStopping);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"A second start will not find this Kiler: {ex.Message}");
}

Browser.OpenOrPrint(browser, self.EntryAddress, options.NoBrowser, Console.Out, Console.Error);

await app.WaitForShutdownAsync();
if (app.Services.GetRequiredService<Heartbeat>().TimedOut)
{
    Console.WriteLine("No Kiler page was open, so Kiler has stopped.");
}

return 0;
