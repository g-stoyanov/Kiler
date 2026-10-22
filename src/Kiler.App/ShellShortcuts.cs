using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Kiler.App;

/// <summary>Makes shortcuts. Tests replace it, so that the Start menu stays as it is.</summary>
internal interface IShortcuts
{
    /// <summary>Makes a shortcut at <paramref name="path"/> that starts <paramref name="target"/> in its own folder.</summary>
    void Create(string path, string target);
}

/// <summary>
/// A <c>.lnk</c> file made by the Windows shell through <c>IShellLinkW</c>. Native AOT has no built-in COM, so the interfaces
/// come from the COM source generator, and <see cref="UniqueComInterfaceMarshaller{T}"/> wraps the object.
/// </summary>
internal sealed partial class ShellShortcuts : IShortcuts
{
    private const uint ClassContextInProcessServer = 0x1;
    private const uint MultiThreaded = 0x0;
    private const int ChangedMode = unchecked((int)0x80010106);

    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkInterface = new("000214F9-0000-0000-C000-000000000046");

    public void Create(string path, string target)
    {
        // The thread may not be in a COM apartment yet. ShellLink is registered for both kinds, so a thread that is already
        // in a single-threaded one (RPC_E_CHANGED_MODE) stays as it is.
        var initialized = CoInitializeEx(0, MultiThreaded);
        if (initialized < 0 && initialized != ChangedMode)
        {
            Marshal.ThrowExceptionForHR(initialized);
        }

        try
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(ShellLinkClass, 0, ClassContextInProcessServer, ShellLinkInterface, out var link));
            try
            {
                link.SetPath(target);
                link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
                link.SetDescription("Kiler");
                ((IPersistFile)link).Save(path, remember: true);
            }
            // A unique wrapper, so it lets the object go now rather than on a later garbage collection.
            finally
            {
                ((ComObject)(object)link).FinalRelease();
            }
        }
        finally
        {
            if (initialized >= 0)
            {
                CoUninitialize();
            }
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid classId,
        nint outer,
        uint classContext,
        in Guid interfaceId,
        [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IShellLinkW>))] out IShellLinkW instance);
}

/// <summary>The methods of <c>IShellLinkW</c> in the order of its vtable. Only the setters that Kiler uses take strings.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal partial interface IShellLinkW
{
    void GetPath(nint file, int length, nint findData, uint flags);

    void GetIDList(out nint idList);

    void SetIDList(nint idList);

    void GetDescription(nint name, int length);

    void SetDescription(string name);

    void GetWorkingDirectory(nint directory, int length);

    void SetWorkingDirectory(string directory);

    void GetArguments(nint arguments, int length);

    void SetArguments(string arguments);

    void GetHotkey(out ushort hotkey);

    void SetHotkey(ushort hotkey);

    void GetShowCmd(out int showCommand);

    void SetShowCmd(int showCommand);

    void GetIconLocation(nint iconPath, int length, out int icon);

    void SetIconLocation(string iconPath, int icon);

    void SetRelativePath(string relativePath, uint reserved);

    void Resolve(nint window, uint flags);

    void SetPath(string file);
}

/// <summary><c>IPersistFile</c>, with the one method of <c>IPersist</c> first, as in its vtable.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000010b-0000-0000-C000-000000000046")]
internal partial interface IPersistFile
{
    void GetClassID(out Guid classId);

    [PreserveSig]
    int IsDirty();

    void Load(string fileName, uint mode);

    void Save(string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

    void SaveCompleted(string fileName);

    void GetCurFile(out nint fileName);
}
