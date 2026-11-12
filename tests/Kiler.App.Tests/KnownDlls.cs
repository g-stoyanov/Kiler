using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kiler.App.Tests;

/// <summary>
/// The names in the object directory <c>\KnownDlls</c> (M3.8b): the DLLs that Windows maps from there and never searches
/// for, the KnownDLLs of the registry and their dependencies. On the developer machine it holds <c>CRYPT32.dll</c> and
/// <c>kernelbase.dll</c>, which the registry list does not.
/// </summary>
internal static class KnownDlls
{
    private const string DirectoryName = @"\KnownDlls";
    private const uint DirectoryQuery = 0x1;
    private const uint CaseInsensitive = 0x40;
    private const int BufferSize = 64 * 1024;

    public static HashSet<string> Names()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = Marshal.StringToHGlobalUni(DirectoryName);
        var name = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            var length = (ushort)(DirectoryName.Length * sizeof(char));
            Marshal.StructureToPtr(new UnicodeString(length, length, text), name, fDeleteOld: false);
            var attributes = new ObjectAttributes(Marshal.SizeOf<ObjectAttributes>(), 0, name, CaseInsensitive, 0, 0);
            var status = NtOpenDirectoryObject(out var handle, DirectoryQuery, in attributes);
            Assert.True(status == 0, $"NtOpenDirectoryObject failed: 0x{status:X8}.");
            using var directory = handle;

            // One entry a call; STATUS_NO_MORE_ENTRIES ends it.
            uint context = 0;
            while (NtQueryDirectoryObject(directory, buffer, BufferSize, returnSingleEntry: true, restartScan: false, ref context, out _) == 0)
            {
                var entry = Marshal.PtrToStructure<UnicodeString>(buffer);
                names.Add(Marshal.PtrToStringUni(entry.Buffer, entry.Length / sizeof(char)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(text);
        }

        return names;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct UnicodeString(ushort Length, ushort MaximumLength, nint Buffer);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ObjectAttributes(int Length, nint RootDirectory, nint ObjectName, uint Attributes, nint SecurityDescriptor, nint SecurityQualityOfService);

    [DllImport("ntdll.dll")]
    private static extern int NtOpenDirectoryObject(out SafeFileHandle directory, uint desiredAccess, in ObjectAttributes attributes);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryObject(
        SafeFileHandle directory, nint buffer, int length, [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
        [MarshalAs(UnmanagedType.U1)] bool restartScan, ref uint context, out uint returnLength);
}
