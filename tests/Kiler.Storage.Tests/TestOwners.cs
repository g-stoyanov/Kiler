using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kiler.Storage.Tests;

/// <summary>
/// A fake of the owners that Windows records (M2.6). The owner of a file or a folder is the one the test gave it, found
/// through the handle by the id of the file (<see cref="NativeFiles.Id"/>): it follows the file through a move, and another
/// file put at its name has its own. One that was given none has <see cref="Default"/>. As the owners of Windows, it gives
/// none for a handle without <c>READ_CONTROL</c>, so the tests also see a handle of the layer that could not read the real
/// owner. The SIDs are made up.
/// </summary>
internal sealed class TestOwners : IFileOwner
{
    /// <summary>The owner of what the test process makes, unless a test gives another one.</summary>
    public const string Me = "S-1-5-21-1000-2000-3000-1001";

    /// <summary>Another account.</summary>
    public const string Other = "S-1-5-21-1000-2000-3000-1002";

    private const uint ReadControl = 0x00020000;

    /// <summary><c>ObjectBasicInformation</c> of <c>OBJECT_INFORMATION_CLASS</c>.</summary>
    private const int BasicInformation = 0;

    private readonly ConcurrentDictionary<FileId, string?> _owners = new();

    /// <summary>The owner of a file or a folder that was given none; <c>null</c> when such an owner cannot be read.</summary>
    public string? Default { get; set; } = Me;

    /// <summary>Gives the file or the folder at the path an owner, or none that can be read with <c>null</c>.</summary>
    public void Set(string path, string? owner)
    {
        using var handle = Directory.Exists(path)
            ? NativeFiles.OpenFolder(path)
            : NativeFiles.OpenFileForTimes(path, toSet: false) ?? throw new FileNotFoundException("No file to give an owner.", path);
        _owners[NativeFiles.Id(handle)] = owner;
    }

    public string? Of(SafeFileHandle file)
    {
        if ((GrantedAccess(file) & ReadControl) == 0)
        {
            return null;
        }

        return _owners.TryGetValue(NativeFiles.Id(file), out var owner) ? owner : Default;
    }

    private static uint GrantedAccess(SafeFileHandle handle)
    {
        var status = NtQueryObject(handle, BasicInformation, out var information, Marshal.SizeOf<ObjectBasicInformation>(), out _);
        return status >= 0 ? information.GrantedAccess : throw new IOException($"NtQueryObject failed: 0x{status:X8}");
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(SafeFileHandle handle, int informationClass, out ObjectBasicInformation information, int length, out int returned);

    /// <summary>The start of <c>PUBLIC_OBJECT_BASIC_INFORMATION</c>, with its size.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 56)]
    private struct ObjectBasicInformation
    {
        public uint Attributes;
        public uint GrantedAccess;
    }
}
