using System.Buffers.Binary;
using System.Text;

namespace Kiler.Release.Tests;

/// <summary>
/// Version resources made by hand, for the cases that the compiler does not make: a block is wLength, wValueLength and wType,
/// the key as UTF-16 with its NUL, padding to 4 bytes, the value, and the children, each after padding to 4 bytes.
/// </summary>
internal static class VersionBlocks
{
    public static byte[] Block(string key, byte[] value, int type, int valueLength, params byte[][] children)
    {
        var bytes = new List<byte>(new byte[6]);
        bytes.AddRange(Encoding.Unicode.GetBytes(key + "\0"));
        Pad(bytes);
        bytes.AddRange(value);
        foreach (var child in children)
        {
            Pad(bytes);
            bytes.AddRange(child);
        }

        var block = bytes.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(block, (ushort)block.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), (ushort)valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4), (ushort)type);
        return block;
    }

    /// <summary>The root block, with a VS_FIXEDFILEINFO of zeros but its signature.</summary>
    public static byte[] Root(params byte[][] children)
    {
        var fixedInfo = new byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo, 0xFEEF04BD);
        return Block("VS_VERSION_INFO", fixedInfo, 0, fixedInfo.Length, children);
    }

    public static byte[] StringFileInfo(params byte[][] tables) => Block("StringFileInfo", [], 1, 0, tables);

    public static byte[] Table(params byte[][] strings) => Block("000004b0", [], 1, 0, strings);

    public static byte[] VarFileInfo() =>
        Block("VarFileInfo", [], 1, 0, Block("Translation", [0x00, 0x00, 0xB0, 0x04], 0, 4));

    /// <summary>A string; its length is in characters with the NUL, as the format says, or in bytes, as some compilers write it.</summary>
    public static byte[] Text(string key, string value, bool lengthInBytes = false) =>
        Block(key, Encoding.Unicode.GetBytes(value + "\0"), 1, lengthInBytes ? (value.Length + 1) * 2 : value.Length + 1);

    /// <summary>A root block whose key runs to its end with no NUL.</summary>
    public static byte[] KeyWithoutNul()
    {
        var block = Encoding.Unicode.GetBytes("\0\0\0VS_VERSION_INFO");
        BinaryPrimitives.WriteUInt16LittleEndian(block, (ushort)block.Length);
        return block;
    }

    private static void Pad(List<byte> bytes)
    {
        while (bytes.Count % 4 != 0)
        {
            bytes.Add(0);
        }
    }
}
