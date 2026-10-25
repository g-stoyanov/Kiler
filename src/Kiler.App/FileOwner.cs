using System.Security.AccessControl;
using System.Security.Principal;

namespace Kiler.App;

/// <summary>The owner that Windows records for a file or a folder. <c>--diag</c> checks it on the shared folder (design 7.8).</summary>
internal static class FileOwner
{
    public static SecurityIdentifier Of(FileSystemInfo item)
    {
        FileSystemSecurity security = item switch
        {
            DirectoryInfo folder => folder.GetAccessControl(AccessControlSections.Owner),
            FileInfo file => file.GetAccessControl(AccessControlSections.Owner),
            _ => throw new ArgumentException($"Neither a file nor a folder: {item.GetType().Name}", nameof(item)),
        };
        return security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new InvalidOperationException($"Windows records no owner for {item.FullName}.");
    }
}
