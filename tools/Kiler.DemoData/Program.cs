using System.Diagnostics;
using Kiler.DemoData;

// The demo data (PLAN M8 step 4, M4.10): a made-up Kiler in a data folder, for the demo, the screenshots and npm run dev.
if (args is ["--help"])
{
    Console.WriteLine(DemoOptions.Usage);
    return 0;
}

if (!DemoOptions.TryParse(args, out var options, out var error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(DemoOptions.Usage);
    return 2;
}

// %LOCALAPPDATA%\Kiler from the known folder of Windows, as Kiler takes it (UserFolders in Kiler.App).
var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
if (localAppData.Length == 0)
{
    Console.Error.WriteLine("Windows gives no LocalApplicationData folder for this user.");
    return 1;
}

var target = DemoTarget.For(options.DataFolder, Path.Combine(localAppData, "Kiler"));
var work = Path.Combine(Path.GetTempPath(), "Kiler.DemoData", Guid.NewGuid().ToString("N"));
var took = Stopwatch.StartNew();
try
{
    if (target.Prepare(options.Replace) is { } refused)
    {
        Console.Error.WriteLine(refused);
        return 1;
    }

    Directory.CreateDirectory(work);
    DemoKiler.Make(target.Folder, work, options.Volume);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
{
    Console.Error.WriteLine($"The demo data was not made: {ex.Message}");
    Console.Error.WriteLine("What it made stays; run it again with --replace.");
    return 1;
}
finally
{
    if (Directory.Exists(work))
    {
        Directory.Delete(work, recursive: true);
    }
}

// The demo passwords are made up and public, as the README gives them; no real password is ever printed.
Console.WriteLine($"The demo data is in {target.Folder}, made in {took.Elapsed.TotalSeconds:0.0} s.");
Console.WriteLine($"Levels: {string.Join(", ", DemoKiler.LevelNames)}; guest mode is on; {DemoRecords.CountOf(options.Volume)} records.");
foreach (var user in DemoKiler.Users)
{
    var rights = user.IsAdmin ? "admin" : user.CanEdit ? "editor" : "reader";
    Console.WriteLine($"  {user.Account,-12} {DemoKiler.LevelNames[user.Level],-5} {rights,-6} password {user.Password}");
}

Console.WriteLine("Start Kiler on it with --data <folder> --account <account>; for dev-data, in src\\ui: npm.cmd run dev -- --account demo.admin");
return 0;
