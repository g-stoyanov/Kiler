using System.Text;
using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>
/// The newest sealed version of <c>levels.json</c> and <c>settings.json</c> (M4.16a, the decision "A seal that fails"): the
/// version with the highest number that the public key verifies, among the file on the share, the history of the local copy
/// and the last good bytes that it keeps apart; the sign-in, the rights of an admin, the admin actions and the automatic lock
/// take it.
/// </summary>
public sealed class SealedFilesTests : IDisposable
{
    private const string Admin = "demo.admin";

    private readonly TestKiler _kiler = new(top: 3);
    private readonly TestData _data = new();

    /// <summary>How someone who can write the share changes <c>levels.json</c> outside Kiler.</summary>
    public enum Change
    {
        /// <summary>Its file written in place, at its version, with a level of its own name: the seal no longer verifies.</summary>
        EditedInPlace,

        /// <summary>An older sealed version copied from <c>history\levels\</c> over it (the M4.11 security review, CWE-294).</summary>
        OlderSealedVersion,

        /// <summary>A new version written through a copy, as Kiler writes it, without the seal.</summary>
        NewerNotSealed,

        /// <summary>Its file damaged.</summary>
        Damaged,

        /// <summary>Its file deleted outside the trash.</summary>
        Gone,
    }

    public void Dispose()
    {
        _data.Dispose();
        _kiler.Dispose();
    }

    /// <summary>
    /// Whatever the change, the sign-in takes version 3, which an admin sealed and the copy keeps, with its names, and so do the
    /// rights of an admin, a new team over the copy (a restart) and an admin action that writes no <c>levels.json</c>; the
    /// rename, which writes over the share, refuses it (decided by the user in M4.16).
    /// </summary>
    [Theory]
    [InlineData(Change.EditedInPlace)]
    [InlineData(Change.OlderSealedVersion)]
    [InlineData(Change.NewerNotSealed)]
    [InlineData(Change.Damaged)]
    [InlineData(Change.Gone)]
    public void TheSignIn_OfALevelsJsonChangedOnTheShare_TakesTheSealedVersionThatTheCopyKeeps(Change change)
    {
        var team = SealedTeam();

        Make(change);
        team.Follow(_data.Copy.Load());

        foreach (var files in (TeamFiles[])[team.Files, new Team(_data.Copy).Files])
        {
            var newest = files.SealedLevels(PublicKey);
            Assert.NotNull(newest);
            Assert.Equal((3, false), (newest.Version, newest.OnShare));
            Assert.Equal("Prod", newest.Model.Levels[3].Name);
            Assert.True(files.HasLevels, "The copy keeps a version of levels.json that fits.");
            using var admin = SignInAs(files, Admin);
            Assert.Equal([0, 1, 2, 3], admin.Levels);
            Assert.Null(Access.Check(Need.Admin, admin, files));
            Assert.Equal("Prod", files.LevelsFor(admin)!.Levels[3].Name);
        }

        using var session = SignInAs(team.Files, Admin);
        Assert.Equal(AdminOutcome.LevelsNotSealed, AdminActions.RenameLevel(session, team, 1, "Team").Outcome);
        var made = AdminActions.NewUser(session, team, "demo.new", "Demo New", level: 3, canEdit: false);
        Assert.Equal(AdminOutcome.Done, made.Outcome);
        Assert.Equal([1, 2, 3], made.User!.Keys.Keys.Keys.Order());
    }

    /// <summary>
    /// A Kiler whose copy never saw a sealed version, as a new machine: a <c>levels.json</c> that does not verify refuses the
    /// sign-in, one that cannot be read leaves nothing to check, and an older sealed one is taken, as limit (1) of M3.md takes
    /// an older sealed user file.
    /// </summary>
    [Theory]
    [InlineData(Change.EditedInPlace, SignInRefusal.LevelsNotSealed)]
    [InlineData(Change.NewerNotSealed, SignInRefusal.LevelsNotSealed)]
    [InlineData(Change.Damaged, SignInRefusal.LevelsInvalid)]
    [InlineData(Change.Gone, SignInRefusal.LevelsInvalid)]
    [InlineData(Change.OlderSealedVersion, null)]
    public void TheSignIn_OnACopyThatNeverSawTheSealedVersion_IsRefused_OrTakesTheOlderOne(Change change, SignInRefusal? refusal)
    {
        SealedTeam();
        Make(change);
        var fresh = _data.OpenCopy();
        fresh.Load();
        var files = new Team(fresh).Files;

        var signedIn = SignIn.TryStart(files, Admin, TestKiler.Password, out var session, out var refused);

        using (session)
        {
            Assert.Equal(refusal is null, signedIn);
            if (refusal is { } expected)
            {
                Assert.Equal(expected, refused);
                Assert.Null(files.SealedLevels(PublicKey));
            }
            else
            {
                Assert.Equal((2, true), (files.SealedLevels(PublicKey)!.Version, files.SealedLevels(PublicKey)!.OnShare));
                Assert.Equal("Test", files.LevelsFor(session)!.Levels[3].Name);
            }
        }

        Assert.Equal(change is not (Change.Damaged or Change.Gone), files.HasLevels);
    }

    /// <summary>At the same version, the file on the share; above it, a version of the copy only when its seal verifies.</summary>
    [Fact]
    public void SealedLevels_IsTheHighestVersionThatTheKeyVerifies()
    {
        var team = SealedTeam();
        Assert.Equal((3, true), (team.Files.SealedLevels(PublicKey)!.Version, team.Files.SealedLevels(PublicKey)!.OnShare));

        // A version 4 sealed by a key of someone's own: the copy keeps version 3 in its history.
        using var theirs = AdminKey.New();
        var other = _data.OpenCopy();
        other.Load();
        Assert.Equal(WriteOutcome.Written, other.Replace(LevelsFile.Key, 3, Levels("Mine").Sealed(theirs, 4).ToContent()).Outcome);
        team.Follow(_data.Copy.Load());

        Assert.Equal((3, false), (team.Files.SealedLevels(PublicKey)!.Version, team.Files.SealedLevels(PublicKey)!.OnShare));
        Assert.Equal((4, true), (team.Files.SealedLevels(theirs.PublicKey)!.Version, team.Files.SealedLevels(theirs.PublicKey)!.OnShare));
        Assert.Equal(3, team.Files.SealedLevels(PublicKey)!.Version);

        // The admin's version 5 on the share again.
        Assert.Equal(WriteOutcome.Written, other.Replace(LevelsFile.Key, 4, Levels("Live").Sealed(_kiler.AdminKey, 5).ToContent()).Outcome);
        team.Follow(_data.Copy.Load());

        Assert.Equal((5, true), (team.Files.SealedLevels(PublicKey)!.Version, team.Files.SealedLevels(PublicKey)!.OnShare));
        Assert.Equal("Live", team.Files.SealedLevels(PublicKey)!.Model.Levels[3].Name);
        Assert.Equal(4, team.Files.SealedLevels(theirs.PublicKey)!.Version);
    }

    /// <summary>
    /// The versions that the copy keeps above the sealed one are passed over when their seal does not verify or they do not fit
    /// their model: a version 4 without the seal in the history, and a version 5 that does not fit, kept apart once the file
    /// on the share is damaged.
    /// </summary>
    [Fact]
    public void SealedLevels_PassesOverTheVersionsOfTheCopyThatAreNotSealed_OrDoNotFit()
    {
        var team = SealedTeam();
        var other = _data.OpenCopy();
        other.Load();
        Assert.Equal(WriteOutcome.Written, other.Replace(LevelsFile.Key, 3, Levels("Mine").ToContent()).Outcome);
        Assert.Equal(WriteOutcome.Written, other.Replace(LevelsFile.Key, 4, new JsonObject { ["levels"] = "none" }).Outcome);
        team.Follow(_data.Copy.Load());
        Make(Change.Damaged);
        team.Follow(_data.Copy.Load());

        Assert.Equal((3, false), (team.Files.SealedLevels(PublicKey)!.Version, team.Files.SealedLevels(PublicKey)!.OnShare));
        Assert.True(team.Files.HasLevels, "The copy keeps versions of levels.json that fit.");
    }

    /// <summary>
    /// A load that touches <c>levels.json</c> reads the versions that the copy keeps again, and those that are as before keep
    /// their models, with the answers of their seal checks, so only a new version is checked (the M4.16a security review:
    /// thousands of versions planted in the history would otherwise be checked at each new <see cref="TeamFiles"/>).
    /// </summary>
    [Fact]
    public void TheVersionsThatTheCopyKeeps_AsBefore_KeepTheirModels_WhenALoadReadsThemAgain()
    {
        var team = SealedTeam();
        var other = _data.OpenCopy();
        other.Load();
        Assert.Equal(WriteOutcome.Written, other.Replace(LevelsFile.Key, 3, Levels("Mine").ToContent()).Outcome);
        team.Follow(_data.Copy.Load());
        var before = team.Files.KeptLevels;
        Assert.Equal([3, 2, 1], before.Select(file => file.Version!.Value));

        Assert.Equal(WriteOutcome.Written, other.Replace(LevelsFile.Key, 4, Levels("Ours").ToContent()).Outcome);
        team.Follow(_data.Copy.Load());

        var after = team.Files.KeptLevels;
        Assert.Equal([4, 3, 2, 1], after.Select(file => file.Version!.Value));
        Assert.All(after.Skip(1).Zip(before), pair => Assert.Same(pair.Second, pair.First));
        Assert.Equal(3, team.Files.SealedLevels(PublicKey)!.Version);
    }

    /// <summary>An earlier model of a version is taken again only with the same seal over the same text, never for another file of that version.</summary>
    [Fact]
    public void KeepKnown_TakesAnEarlierModelOfAVersion_OnlyWithTheSameSeal()
    {
        // A seal of ECDSA is new at each sealing, so the same seal is the same file read again.
        var document = VersionTwo(Levels("Test").Sealed(_kiler.AdminKey, 2).ToContent());
        var sealedByAdmin = TeamFile<LevelsFile>.Of(document, LevelsFile.TryRead);
        var readAgain = TeamFile<LevelsFile>.Of(document, LevelsFile.TryRead);
        var sealedAgain = TeamFile<LevelsFile>.Of(VersionTwo(Levels("Test").Sealed(_kiler.AdminKey, 2).ToContent()), LevelsFile.TryRead);
        var otherText = Levels("Mine").ToContent();
        otherText[FileSeal.Member] = sealedByAdmin.Model!.Stamp;
        var stampOverOtherText = TeamFile<LevelsFile>.Of(VersionTwo(otherText), LevelsFile.TryRead);

        Assert.Same(sealedByAdmin, Assert.Single(Team.KeepKnown([readAgain], [sealedByAdmin])));
        Assert.Same(sealedAgain, Assert.Single(Team.KeepKnown([sealedAgain], [sealedByAdmin])));
        Assert.Same(stampOverOtherText, Assert.Single(Team.KeepKnown([stampOverOtherText], [sealedByAdmin])));
        Assert.True(sealedByAdmin.Model.IsSealedBy(PublicKey), "The admin's seal does not verify.");
        Assert.False(stampOverOtherText.Model!.IsSealedBy(PublicKey), "The admin's stamp over another text verifies.");
    }

    /// <summary>A seal remembers the answer for the last key only, so each key gets its own answer when they ask in turn.</summary>
    [Fact]
    public void IsSealedBy_GivesEachKeyItsAnswer_WhenTheyAskInTurn()
    {
        using var theirs = AdminKey.New();
        var levels = TeamFile<LevelsFile>.Of(VersionTwo(Levels("Test").Sealed(_kiler.AdminKey, 2).ToContent()), LevelsFile.TryRead).Model!;

        foreach (var _ in Enumerable.Range(0, 2))
        {
            Assert.True(levels.IsSealedBy(PublicKey), "The admin's seal does not verify.");
            Assert.True(levels.IsSealedBy(PublicKey), "The admin's seal does not verify a second time.");
            Assert.False(levels.IsSealedBy(theirs.PublicKey), "The admin's seal verifies with another key.");
            Assert.False(levels.IsSealedBy(theirs.PublicKey), "The admin's seal verifies with another key a second time.");
        }
    }

    /// <summary>
    /// The minutes of the automatic lock come from the newest <c>settings.json</c> that the admin key sealed (M4.16a): an
    /// unsealed version of a day does not take the place of the admin's 10 minutes that the copy keeps; a copy that never saw
    /// them takes 30.
    /// </summary>
    [Fact]
    public void TheMinutesOfTheLock_OfASettingsJsonChangedOnTheShare_AreThoseOfTheSealedVersionThatTheCopyKeeps()
    {
        var team = SealedTeam();
        var other = _data.OpenCopy();
        other.Load();
        Assert.Equal(WriteOutcome.Written, other.Replace(SettingsFile.Key, 1, new SettingsFile(guestMode: false, autoLockMinutes: 10).Sealed(_kiler.AdminKey, 2).ToContent()).Outcome);
        team.Follow(_data.Copy.Load());
        Assert.Equal(10, AutoLock.MinutesOf(team.Files, PublicKey));

        Assert.Equal(WriteOutcome.Written, other.Replace(SettingsFile.Key, 2, new SettingsFile(guestMode: false, autoLockMinutes: SettingsFile.MaxAutoLockMinutes).ToContent()).Outcome);
        team.Follow(_data.Copy.Load());

        Assert.Equal(SettingsFile.MaxAutoLockMinutes, team.Files.Settings!.Model!.AutoLockMinutes);
        Assert.Equal(10, AutoLock.MinutesOf(team.Files, PublicKey));
        Assert.Equal(10, AutoLock.MinutesOf(new Team(_data.Copy).Files, PublicKey));
        var fresh = _data.OpenCopy();
        fresh.Load();
        Assert.Equal(AutoLock.DefaultMinutes, AutoLock.MinutesOf(new Team(fresh).Files, PublicKey));
    }

    /// <summary>A guest has no key to check a seal with, so it takes the <c>levels.json</c> of the share as it is (M4.16a).</summary>
    [Fact]
    public void LevelsFor_AGuest_IsTheFileOnTheShare()
    {
        var team = SealedTeam();
        Make(Change.NewerNotSealed);
        team.Follow(_data.Copy.Load());

        Assert.Equal("Mine", team.Files.LevelsFor(null)!.Levels[3].Name);
        Assert.Equal("Mine", team.Files.Levels!.Model!.Levels[3].Name);
    }

    private string PublicKey => _kiler.AdminKey.PublicKey;

    /// <summary>
    /// A team whose <c>levels.json</c> an admin renamed twice, each version sealed (the top level "Test" at version 2, "Prod" at
    /// version 3), with sealed settings and the admin's user file.
    /// </summary>
    private Team SealedTeam()
    {
        Create(LevelsFile.Key, _kiler.Levels.ToContent());
        Replace(1, Levels("Test").Sealed(_kiler.AdminKey, 2));
        Replace(2, Levels("Prod").Sealed(_kiler.AdminKey, 3));
        Create(SettingsFile.Key, new SettingsFile(guestMode: false, autoLockMinutes: 30).Sealed(_kiler.AdminKey, 1).ToContent());
        Create(UserFile.KeyOf(Admin), _kiler.User(Admin, level: 3, canEdit: true, isAdmin: true, adminKey: AdminKeyLock.PublicAndPrivateKey, seal: true).ToContent());
        var team = new Team(_data.Copy);
        Assert.Equal(3, team.Files.Levels!.Version);
        return team;
    }

    /// <summary>The levels of the made-up Kiler, with the top level named <paramref name="top"/>, not sealed.</summary>
    private LevelsFile Levels(string top) =>
        new([.. _kiler.Levels.Levels.Select(entry => entry.Level == 3 ? entry with { Name = top } : entry)], _kiler.Levels.Recovery);

    /// <summary><paramref name="content"/> as version 2 of <c>levels.json</c> of a data folder of its own, read as the copy gives a version.</summary>
    private Document VersionTwo(JsonObject content)
    {
        using var data = new TestData();
        data.WriteAndRead(LevelsFile.Key, _kiler.Levels.ToContent());
        return data.ReplaceAndRead(LevelsFile.Key, 1, content);
    }

    /// <summary>Changes <c>levels.json</c> of the share at version 3 outside the copy of the team.</summary>
    private void Make(Change change)
    {
        var path = Path.Combine(_data.Folder, "levels.json");
        switch (change)
        {
            case Change.EditedInPlace:
                File.WriteAllText(path, File.ReadAllText(path, Encoding.UTF8).Replace("\"Prod\"", "\"Mine\"", StringComparison.Ordinal), new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(path, File.GetCreationTimeUtc(path).AddMinutes(1));
                break;
            case Change.OlderSealedVersion:
                var older = File.ReadAllBytes(Path.Combine(_data.Folder, "history", "levels", "v2.json"));
                File.Delete(path);
                File.WriteAllBytes(path, older);
                File.SetCreationTimeUtc(path, File.GetLastWriteTimeUtc(path));
                break;
            case Change.NewerNotSealed:
                var other = _data.OpenCopy();
                other.Load();
                Assert.Equal(WriteOutcome.Written, other.Replace(LevelsFile.Key, 3, Levels("Mine").ToContent()).Outcome);
                break;
            case Change.Damaged:
                File.WriteAllText(path, "{", Encoding.UTF8);
                break;
            case Change.Gone:
                File.Delete(path);
                break;
        }
    }

    private static Session SignInAs(TeamFiles files, string account)
    {
        Assert.True(SignIn.TryStart(files, account, TestKiler.Password, out var session, out var refusal), $"The sign-in was refused: {refusal}.");
        return session;
    }

    private void Create(DocumentKey key, JsonObject content) => Assert.Equal(WriteOutcome.Written, _data.Copy.Create(key, content).Outcome);

    private void Replace(int version, LevelsFile levels) => Assert.Equal(WriteOutcome.Written, _data.Copy.Replace(LevelsFile.Key, version, levels.ToContent()).Outcome);
}
