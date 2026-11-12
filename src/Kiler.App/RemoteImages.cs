using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kiler.App;

/// <summary>
/// The DLLs of the folder of the exe, where anyone who writes the shared folder can put one (M3.8, M3.8b). The process
/// mitigation <c>NoRemoteImages</c>: once it is on, Windows maps no image (an exe or a DLL) from a network path into the
/// process. The imports of the exe and Kiler's own P/Invokes look only in System32 (M1.8), but the runtime and the system
/// DLLs still load some DLLs by name, from the folder of the exe first. The policy cannot be turned off again, and it covers
/// only the images mapped after it, so the exe delay-loads <c>ncrypt.dll</c>, <c>CRYPT32.dll</c> and <c>Secur32.dll</c>
/// (Kiler.App.csproj), for which the Windows loader took <c>NTASN1.dll</c> and <c>SspiCli.dll</c> from there before
/// <c>Main</c>. A refused image ends the search, so a planted copy would fail the load (measured in M3.8b), and
/// <c>PreferSystem32Images</c> set after the start did not change it: so the policy is followed by a load of those DLLs from
/// System32 (<see cref="SystemDlls"/>), which their later loads by name then take without a search.
/// </summary>
internal static partial class RemoteImages
{
    /// <summary><c>ProcessImageLoadPolicy</c> of <c>PROCESS_MITIGATION_POLICY</c>.</summary>
    private const int ImageLoadPolicy = 10;

    /// <summary>The bit <c>NoRemoteImages</c> of <c>PROCESS_MITIGATION_IMAGE_LOAD_POLICY</c>.</summary>
    private const uint NoRemoteImages = 0x1;

    /// <summary><c>LOAD_LIBRARY_SEARCH_SYSTEM32</c>: System32 only, for the DLL and for what it imports.</summary>
    private const uint LoadLibrarySearchSystem32 = 0x800;

    /// <summary>
    /// The DLLs that the exe delay-loads, each after what its first call loads by name: <c>SspiCli.dll</c>, where
    /// <c>Secur32!GetUserNameExW</c> forwards, and <c>NTASN1.dll</c>, which <c>ncrypt.dll</c> delay-loads. Windows uses a DLL
    /// that is loaded already when a load names it without a path, and does not search for it (the DLL search order of the
    /// Windows docs), so a copy of one of these next to the exe is never tried, in the shared folder or in the local copy.
    /// </summary>
    internal static readonly IReadOnlyList<string> SystemDlls = ["SspiCli.dll", "NTASN1.dll", "Secur32.dll", "CRYPT32.dll", "ncrypt.dll"];

    /// <summary>
    /// Turns the policy on, then loads <see cref="SystemDlls"/> from System32, at every start (decided by the user in M3.8
    /// and M3.8b): Kiler loads nothing from a network, so it costs nothing, and every run of the published exe in the smoke
    /// and E2E tests has it. A failure is a line on <paramref name="errors"/>; false when Kiler must not go on, which is a
    /// start from a network folder.
    /// </summary>
    public static bool Forbid(string? exePath, TextWriter errors) => Forbid(TurnOn, LoadFromSystem32, exePath, errors);

    /// <param name="turnOn">Turns the policy on: 0, or the Win32 error.</param>
    /// <param name="load">Loads a DLL of <see cref="SystemDlls"/> from System32: 0, or the Win32 error.</param>
    /// <param name="exePath">The running exe, as <see cref="LocalCopy.Decide"/> takes it.</param>
    /// <param name="errors">Where a failure is said.</param>
    internal static bool Forbid(Func<int> turnOn, Func<string, int> load, string? exePath, TextWriter errors)
    {
        // A path that cannot be told is local, as for the local copy.
        var fromNetwork = exePath is not null && CopyFiles.IsNetworkPath(exePath);

        if (turnOn() is var error and not 0)
        {
            var reason = new Win32Exception(error).Message;
            if (fromNetwork)
            {
                errors.WriteLine($"Kiler does not start from the shared folder: it cannot forbid DLLs from a network path. {reason}");
                return false;
            }

            errors.WriteLine($"Kiler cannot forbid DLLs from a network path: {reason}");
        }

        foreach (var name in SystemDlls)
        {
            if (load(name) is var loadError and not 0)
            {
                var reason = new Win32Exception(loadError).Message;
                if (fromNetwork)
                {
                    errors.WriteLine($"Kiler does not start from the shared folder: it cannot load {name} from System32. {reason}");
                    return false;
                }

                errors.WriteLine($"Kiler cannot load {name} from System32: {reason}");
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="process"/> has the policy on; it throws <see cref="Win32Exception"/> when it cannot tell.</summary>
    public static bool AreForbidden(SafeProcessHandle process)
    {
        if (!GetProcessMitigationPolicy(process, ImageLoadPolicy, out var flags, sizeof(uint)))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return (flags & NoRemoteImages) != 0;
    }

    private static int TurnOn()
    {
        var flags = NoRemoteImages;
        return SetProcessMitigationPolicy(ImageLoadPolicy, in flags, sizeof(uint)) ? 0 : Marshal.GetLastPInvokeError();
    }

    /// <summary>The module stays loaded for the life of the process: its handle is never freed.</summary>
    private static int LoadFromSystem32(string name) =>
        LoadLibraryEx(name, 0, LoadLibrarySearchSystem32) != 0 ? 0 : Marshal.GetLastPInvokeError();

    /// <summary><c>PROCESS_MITIGATION_IMAGE_LOAD_POLICY</c> is a union of a DWORD of flags and its bit fields, so it is passed as that DWORD.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessMitigationPolicy(int policy, in uint buffer, nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMitigationPolicy(SafeProcessHandle process, int policy, out uint buffer, nuint length);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryEx(string fileName, nint file, uint flags);
}
