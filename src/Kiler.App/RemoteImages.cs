using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kiler.App;

/// <summary>
/// The process mitigation <c>NoRemoteImages</c> (M3.8): once it is on, Windows maps no image (an exe or a DLL) from a
/// network path into the process, such as a DLL that anyone who writes the shared folder can put next to Kiler.exe. The
/// imports of the exe and Kiler's own P/Invokes look only in System32 (M1.8), but the runtime and the system DLLs still
/// load some DLLs from the folder of the exe (M1.8, the checkpoint after M1). It cannot be turned off again, and it covers
/// only the images mapped after it: not <c>NTASN1.dll</c> and <c>SspiCli.dll</c>, which the Windows loader takes from the folder
/// of the exe before <c>Main</c>, for the static imports <c>ncrypt.dll</c> and <c>CRYPT32.dll</c> of the exe (measured in M3.8).
/// </summary>
internal static partial class RemoteImages
{
    /// <summary><c>ProcessImageLoadPolicy</c> of <c>PROCESS_MITIGATION_POLICY</c>.</summary>
    private const int ImageLoadPolicy = 10;

    /// <summary>The bit <c>NoRemoteImages</c> of <c>PROCESS_MITIGATION_IMAGE_LOAD_POLICY</c>.</summary>
    private const uint NoRemoteImages = 0x1;

    /// <summary>
    /// Turns the policy on, at every start (decided by the user in M3.8): Kiler loads nothing from a network, so it costs
    /// nothing, and every run of the published exe in the smoke and E2E tests has it. A failure is a line on
    /// <paramref name="errors"/>; false when Kiler must not go on, which is a start from a network folder.
    /// </summary>
    public static bool Forbid(string? exePath, TextWriter errors) => Forbid(TurnOn, exePath, errors);

    /// <param name="turnOn">Turns the policy on: 0, or the Win32 error.</param>
    /// <param name="exePath">The running exe, as <see cref="LocalCopy.Decide"/> takes it.</param>
    /// <param name="errors">Where a failure is said.</param>
    internal static bool Forbid(Func<int> turnOn, string? exePath, TextWriter errors)
    {
        var error = turnOn();
        if (error == 0)
        {
            return true;
        }

        var reason = new Win32Exception(error).Message;

        // A path that cannot be told is local, as for the local copy.
        if (exePath is not null && CopyFiles.IsNetworkPath(exePath))
        {
            errors.WriteLine($"Kiler does not start from the shared folder: it cannot forbid DLLs from a network path. {reason}");
            return false;
        }

        errors.WriteLine($"Kiler cannot forbid DLLs from a network path: {reason}");
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

    /// <summary><c>PROCESS_MITIGATION_IMAGE_LOAD_POLICY</c> is a union of a DWORD of flags and its bit fields, so it is passed as that DWORD.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessMitigationPolicy(int policy, in uint buffer, nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMitigationPolicy(SafeProcessHandle process, int policy, out uint buffer, nuint length);
}
