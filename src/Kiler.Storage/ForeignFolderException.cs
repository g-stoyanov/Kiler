namespace Kiler.Storage;

/// <summary>
/// A private folder, or a folder in it, that is not the one Kiler knows (M2.7d, design 7.8): its root has another owner or
/// another id than when it was made or first seen, or a folder in it has another owner than the root. Someone renamed the
/// folder away and put another at its name, or took ownership of it. Nothing is read from it or written to it, and the local
/// copy keeps its documents as it had them: a private folder until the folder at the name is accepted
/// (<see cref="DataCopy.AcceptPrivateFolder"/>), a folder in it until it is put right on the share. It is not a share that
/// cannot be reached, so a write that throws it is not tried again.
/// </summary>
public sealed class ForeignFolderException : IOException
{
    public ForeignFolderException(string message)
        : base(message)
    {
    }
}
