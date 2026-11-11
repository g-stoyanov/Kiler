using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace Kiler.Release.Tests;

/// <summary>Changes of the bytes of <see cref="ToolFolder.ToolAssembly"/>, for the exes that the tool refuses.</summary>
internal static class PeBytes
{
    /// <summary>The product version of the tool's assembly, as the version resource holds it.</summary>
    public static string ProductVersion =>
        typeof(ReleaseTool).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    /// <summary>Sets the resource directory of the PE header to zero: a PE without resources.</summary>
    public static void RemoveResourceDirectory(byte[] bytes)
    {
        var headers = new PEHeaders(new MemoryStream(bytes));
        Assert.NotEqual(0, headers.PEHeader!.ResourceTableDirectory.Size);

        // The data directories follow the optional header's 96 bytes (PE32) or 112 (PE32+); the resources are the third.
        var directories = headers.PEHeaderStartOffset + (headers.PEHeader.Magic == PEMagic.PE32Plus ? 112 : 96);
        bytes.AsSpan(directories + (2 * 8), 8).Clear();
        Assert.Equal(0, new PEHeaders(new MemoryStream(bytes)).PEHeader!.ResourceTableDirectory.Size);
    }

    /// <summary>Replaces the character at <paramref name="index"/> of the product version in the one place it is as UTF-16.</summary>
    public static void ReplaceProductVersion(byte[] bytes, int index, char replacement)
    {
        var text = ProductVersion;
        Assert.InRange(index, 0, text.Length - 1);
        var utf16 = MemoryMarshal.AsBytes(text.AsSpan());
        var at = bytes.AsSpan().IndexOf(utf16);
        Assert.True(at >= 0, "The product version is not in the assembly as UTF-16.");
        Assert.True(bytes.AsSpan(at + 1).IndexOf(utf16) < 0, "The product version is in the assembly twice.");
        BitConverter.TryWriteBytes(bytes.AsSpan(at + (index * 2), 2), replacement);
    }
}
