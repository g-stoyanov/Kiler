using Microsoft.Win32.SafeHandles;

namespace Kiler.Storage;

/// <summary>
/// The owner that the file system records for a file or a folder (M2.6, design 7.8): the account that made it, as the shared
/// folder keeps it, since every write of Kiler makes its file anew. Kiler.App implements it on Windows. It is the exception
/// to the Windows parts that are interfaces in Kiler.Core (PLAN section 3), as the layer reads the owner through the handle
/// it reads the file with, so a link or another file put at the name is never asked instead. Only the history slots in a
/// list of versions are opened for their owners alone (<see cref="DocumentVersion"/>).
/// </summary>
public interface IFileOwner
{
    /// <summary>
    /// The owner of the file or the folder of an open handle, as a SID string such as <c>S-1-5-21-…-1001</c>; <c>null</c>
    /// when it cannot be read: the handle has no right to it (<c>READ_CONTROL</c>), the file system keeps no owners, or none
    /// is recorded. It never throws for that.
    /// </summary>
    string? Of(SafeFileHandle file);
}
