namespace Kiler.Core;

/// <summary>
/// The newest version of a whole file of the team that an admin sealed (M4.16a, the decision "A seal that fails" in
/// <c>docs/tasks/M4.md</c>): the version with the highest number that the public key verifies, among the file on the share and
/// the versions that the local copy keeps, its history and the last good bytes that it keeps apart for a change outside Kiler
/// (<see cref="TeamFiles.SealedLevels"/>, <see cref="TeamFiles.SealedSettings"/>). The seal covers the version, so an older
/// sealed version that someone copies from <c>history\</c> over the current one is not taken while the copy keeps a later one.
/// </summary>
/// <param name="Model">The model of that version.</param>
/// <param name="Version">Its version.</param>
/// <param name="OnShare">
/// Whether it is the file on the share, as the team has it. A write through the copy makes only the version of the share plus
/// one, so an admin action writes over a sealed version only when it is on the share (decided by the user in M4.16).
/// </param>
public sealed record SealedFile<T>(T Model, int Version, bool OnShare)
    where T : class;

/// <summary>A model of a whole file that the admin seal covers with its version (M4.11): <c>levels.json</c>, <c>settings.json</c> and a template (M4.16b).</summary>
internal interface ISealedFile
{
    /// <summary>The seal of the file as it was read, or as it was sealed; <c>null</c> without a <c>stamp</c>.</summary>
    FileSeal? Seal { get; }

    /// <summary>Whether the seal of the file as it was read verifies with <paramref name="publicKey"/>.</summary>
    bool IsSealedBy(string publicKey);
}
