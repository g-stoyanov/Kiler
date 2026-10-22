using System.Security.AccessControl;
using System.Security.Principal;

namespace Kiler.App.Tests;

public sealed class PrivateFolderTests : IDisposable
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);

    private readonly TempFolder _folder = new();
    private readonly SecurityIdentifier _user;

    public PrivateFolderTests()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _user = identity.User!;
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Create_AMissingFolder_GivesFullControlOnlyToTheUserAndSystemAndInheritsNothing()
    {
        var path = Path.Combine(_folder.Path, "Kiler");

        PrivateFolder.Create(path);

        var security = new DirectoryInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = Rules(security);
        Assert.Equal(new HashSet<SecurityIdentifier> { _user, System }, rules.Select(rule => (SecurityIdentifier)rule.IdentityReference).ToHashSet());
        Assert.All(rules, rule =>
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
        });
    }

    [Fact]
    public void WriteFile_TheFileInheritsOnlyTheUserAndSystem()
    {
        var path = Path.Combine(_folder.Path, "Kiler", "instance.json");

        PrivateFolder.WriteFile(path, [1, 2, 3]);

        var rules = Rules(new FileInfo(path).GetAccessControl());
        Assert.Equal(new HashSet<SecurityIdentifier> { _user, System }, rules.Select(rule => (SecurityIdentifier)rule.IdentityReference).ToHashSet());
        Assert.All(rules, rule => Assert.True(rule.IsInherited));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
    }

    [Fact]
    public void Create_AFolderThatExists_IsLeftAsItIs()
    {
        var path = Path.Combine(_folder.Path, "Kiler");
        Directory.CreateDirectory(path);
        var before = new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        PrivateFolder.Create(path);

        var after = new DirectoryInfo(path).GetAccessControl();
        Assert.False(after.AreAccessRulesProtected);
        Assert.Equal(before, after.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Fact]
    public void Create_ByManyStartsAtTheSameMoment_MakesOnePrivateFolderWithoutAnError()
    {
        // Two double clicks on a clean machine: both see no folder, and both make it.
        for (var round = 0; round < 20; round++)
        {
            var path = Path.Combine(_folder.Path, $"Kiler{round}");
            using var go = new Barrier(8);

            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
            {
                go.SignalAndWait(TestContext.Current.CancellationToken);
                PrivateFolder.Create(path);
            });

            Assert.True(new DirectoryInfo(path).GetAccessControl().AreAccessRulesProtected);
        }
    }

    [Fact]
    public void Create_AMissingParent_IsMadeAsUsualAndOnlyTheFolderIsPrivate()
    {
        var parent = Path.Combine(_folder.Path, "Local");
        var path = Path.Combine(parent, "Kiler");

        PrivateFolder.Create(path);

        Assert.False(new DirectoryInfo(parent).GetAccessControl().AreAccessRulesProtected);
        Assert.True(new DirectoryInfo(path).GetAccessControl().AreAccessRulesProtected);
    }

    private static List<FileSystemAccessRule> Rules(FileSystemSecurity security) =>
        [.. security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()];
}
