using Kiler.Core;
using Kiler.Storage;
using Microsoft.Win32.SafeHandles;

namespace Kiler.DemoData.Tests;

/// <summary>
/// A demo Kiler that <see cref="DemoKiler.Make"/> writes into a temporary folder, read through a new local copy and
/// <see cref="Team"/>, as Kiler opens it at its first start; the folder is deleted with it. A test class makes it once, as
/// each demo user costs three PBKDF2 and the volume mode some 2000 writes.
/// </summary>
public abstract class DemoFolder : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Kiler.Tests", Guid.NewGuid().ToString("N"));
    private readonly DataCopy _copy;
    private readonly Team _team;

    protected DemoFolder(bool volume)
    {
        var data = Path.Combine(_folder, "data");
        var work = Directory.CreateDirectory(Path.Combine(_folder, "work")).FullName;
        DemoKiler.Make(data, work, volume);

        _copy = DataCopy.Open(DataFolder.Open(data, new NoOwners()), Directory.CreateDirectory(Path.Combine(_folder, "copy")).FullName, TimeProvider.System);
        var load = _copy.Load();
        Assert.Empty(load.Outside);
        Assert.Empty(load.Skipped);
        _team = new Team(_copy);
    }

    public TeamFiles Files => _team.Files;

    /// <summary>The stashes and the records as <paramref name="session"/> reads them, or a guest when it is null, through <see cref="Records"/> as Kiler has it.</summary>
    internal RecordSet Read(Session? session)
    {
        using var current = new CurrentSession();
        using var records = new Records(_copy, _team, current);
        return records.Read(session);
    }

    /// <summary>The session of <paramref name="user"/>, signed in with the demo password.</summary>
    internal Session SignIn(DemoUser user)
    {
        Assert.True(Core.SignIn.TryStart(Files, user.Account, user.Password, out var session, out var refusal), $"{user.Account} did not sign in: {refusal}.");
        return session;
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class NoOwners : IFileOwner
    {
        public string? Of(SafeFileHandle file) => null;
    }
}
