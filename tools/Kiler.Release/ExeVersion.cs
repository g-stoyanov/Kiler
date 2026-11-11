using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using Kiler.Crypto;

namespace Kiler.Release;

/// <summary>
/// The version of an exe, read from its bytes (M3.5): the <c>ProductVersion</c> of its version resource without the build
/// metadata after "+", which must be <c>major.minor.patch</c> (<see cref="ReleaseVersion"/>). The compiler writes the
/// informational version there, and Native AOT copies the resources of the managed assembly into Kiler.exe
/// (<c>--win32resourcemodule</c> of ILCompiler, unless <c>IlcGenerateWin32Resources</c> is false), so it is the text that
/// Kiler shows as its own version (<c>HealthApi.Version</c>): 1.0.0 for <c>1.0.0+&lt;commit&gt;</c>. The bytes are those
/// that <c>sign</c> hashes, so the version and the hash come from one read of the exe.
/// </summary>
internal static class ExeVersion
{
    /// <summary>RT_VERSION, the type of the version resource.</summary>
    private const uint VersionType = 16;

    private const int DirectorySize = 16;

    private const int EntrySize = 8;

    private const int DataEntrySize = 16;

    private const uint SubdirectoryFlag = 0x8000_0000;

    /// <summary>wLength, wValueLength and wType of a block of the version resource.</summary>
    private const int BlockHeaderSize = 6;

    private const int TextType = 1;

    /// <summary>The keys from the root block down to the product version; null takes every string table.</summary>
    private static readonly string?[] ProductVersionPath = ["StringFileInfo", null, "ProductVersion"];

    /// <summary>The version of <paramref name="exe"/>; false when it has no version resource or another form of version.</summary>
    public static bool TryRead(byte[] exe, out ReleaseVersion version)
    {
        version = default;
        return TryGetVersionResource(exe, out var resource) && TryParse(ProductVersion(resource), out version);
    }

    /// <summary>The version of a product version, such as <c>1.0.0+&lt;commit&gt;</c>: the text before "+", as <c>major.minor.patch</c>.</summary>
    internal static bool TryParse(string? productVersion, out ReleaseVersion version)
    {
        version = default;
        if (productVersion is null)
        {
            return false;
        }

        var metadata = productVersion.IndexOf('+', StringComparison.Ordinal);
        return ReleaseVersion.TryParse(metadata < 0 ? productVersion : productVersion[..metadata], out version);
    }

    /// <summary>
    /// The text of <c>ProductVersion</c> in the string tables of a version resource; null when there is none, when two tables
    /// differ, or when a block is damaged.
    /// </summary>
    internal static string? ProductVersion(ReadOnlySpan<byte> resource)
    {
        string? found = null;
        return TryReadBlock(resource, out var root)
            && string.Equals(root.Key, "VS_VERSION_INFO", StringComparison.Ordinal)
            && Collect(root.Children, ProductVersionPath, ref found)
            ? found
            : null;
    }

    /// <summary>
    /// The bytes of the version resource of a PE: the resource directory of its headers, its three levels (the type, the name
    /// and the language), and the data that the entry of the first language names. False for bytes that are not a PE, and for
    /// a PE that has no version resource or whose directory points outside the bytes.
    /// </summary>
    internal static bool TryGetVersionResource(byte[] exe, out ReadOnlySpan<byte> resource)
    {
        resource = default;
        PEHeaders headers;
        try
        {
            using var stream = new MemoryStream(exe, writable: false);
            headers = new PEHeaders(stream);
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or ArgumentException)
        {
            return false;
        }

        // Bytes without the MZ header are read as a COFF object, which has no PE header.
        if (headers.PEHeader is not { ResourceTableDirectory: var table }
            || !TrySlice(exe, headers, table, out var resources)
            || !TryFindEntry(resources, 0, VersionType, out var names)
            || (names & SubdirectoryFlag) == 0
            || !TryFindEntry(resources, (int)(names & ~SubdirectoryFlag), null, out var languages)
            || (languages & SubdirectoryFlag) == 0
            || !TryFindEntry(resources, (int)(languages & ~SubdirectoryFlag), null, out var data)
            || (data & SubdirectoryFlag) != 0
            || data > resources.Length - DataEntrySize)
        {
            return false;
        }

        // The entry of the data: its RVA and its size, then a code page and a reserved field.
        var entry = resources.Slice((int)data, DataEntrySize);
        var rva = BinaryPrimitives.ReadInt32LittleEndian(entry);
        var size = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
        return TrySlice(exe, headers, new DirectoryEntry(rva, size), out resource);
    }

    /// <summary>The bytes of a range of RVAs, which must be in a section and within the bytes of the file.</summary>
    private static bool TrySlice(byte[] exe, PEHeaders headers, DirectoryEntry range, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (range.Size <= 0 || !headers.TryGetDirectoryOffset(range, out var offset) || offset < 0 || offset > exe.Length - range.Size)
        {
            return false;
        }

        bytes = exe.AsSpan(offset, range.Size);
        return true;
    }

    /// <summary>
    /// The offset field of the entry of the resource directory at <paramref name="directory"/> whose id is
    /// <paramref name="id"/>, or of its first entry when <paramref name="id"/> is null. The entries with a name come first,
    /// then those with an id.
    /// </summary>
    private static bool TryFindEntry(ReadOnlySpan<byte> resources, int directory, uint? id, out uint target)
    {
        target = 0;
        if (directory < 0 || directory > resources.Length - DirectorySize)
        {
            return false;
        }

        int named = BinaryPrimitives.ReadUInt16LittleEndian(resources[(directory + 12)..]);
        int ids = BinaryPrimitives.ReadUInt16LittleEndian(resources[(directory + 14)..]);
        var first = directory + DirectorySize;
        var count = named + ids;
        if (count == 0 || count > (resources.Length - first) / EntrySize)
        {
            return false;
        }

        for (var i = id is null ? 0 : named; i < count; i++)
        {
            var entry = resources.Slice(first + (i * EntrySize), EntrySize);
            if (id is null || BinaryPrimitives.ReadUInt32LittleEndian(entry) == id)
            {
                target = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks the blocks of <paramref name="blocks"/> along the keys of <paramref name="path"/> and keeps the text of the last
    /// key in <paramref name="found"/>. False for a damaged block, and for a second text that differs from the first.
    /// </summary>
    private static bool Collect(ReadOnlySpan<byte> blocks, ReadOnlySpan<string?> path, ref string? found)
    {
        var offset = 0;
        while (offset <= blocks.Length - BlockHeaderSize)
        {
            // A block of length 0 is padding at the end.
            if (BinaryPrimitives.ReadUInt16LittleEndian(blocks[offset..]) == 0)
            {
                break;
            }

            if (!TryReadBlock(blocks[offset..], out var block))
            {
                return false;
            }

            if (path[0] is null || string.Equals(block.Key, path[0], StringComparison.Ordinal))
            {
                if (path.Length > 1)
                {
                    if (!Collect(block.Children, path[1..], ref found))
                    {
                        return false;
                    }
                }
                else
                {
                    var text = block.Text;
                    if (found is not null && !string.Equals(found, text, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    found = text;
                }
            }

            offset = Align(offset + block.Length);
        }

        return true;
    }

    /// <summary>
    /// The block at the start of <paramref name="data"/>: wLength, wValueLength and wType, the key as UTF-16 up to its NUL,
    /// padding to 4 bytes, the value, padding to 4 bytes, and the children up to wLength.
    /// </summary>
    private static bool TryReadBlock(ReadOnlySpan<byte> data, out Block block)
    {
        block = default;
        if (data.Length < BlockHeaderSize)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int valueLength = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        int type = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (length < BlockHeaderSize || length > data.Length)
        {
            return false;
        }

        data = data[..length];
        var keyEnd = NulAt(data, BlockHeaderSize);
        if (keyEnd < 0)
        {
            return false;
        }

        // The length of a text is in characters, but some compilers write bytes; past the block it is cut, and a text is read
        // up to its NUL.
        var valueStart = Math.Min(Align(keyEnd + 2), length);
        var valueBytes = Math.Min(type == TextType ? valueLength * 2 : valueLength, length - valueStart);
        var childrenStart = Math.Min(Align(valueStart + valueBytes), length);
        block = new Block(
            length,
            Encoding.Unicode.GetString(data[BlockHeaderSize..keyEnd]),
            data.Slice(valueStart, valueBytes),
            data[childrenStart..]);
        return true;
    }

    /// <summary>The offset of the first NUL character of UTF-16 from <paramref name="start"/>, or -1.</summary>
    private static int NulAt(ReadOnlySpan<byte> data, int start)
    {
        for (var i = start; i + 1 < data.Length; i += 2)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int Align(int offset) => (offset + 3) & ~3;

    private readonly ref struct Block(int length, string key, ReadOnlySpan<byte> value, ReadOnlySpan<byte> children)
    {
        public int Length { get; } = length;

        public string Key { get; } = key;

        public ReadOnlySpan<byte> Children { get; } = children;

        /// <summary>The value as a text: UTF-16 up to its NUL.</summary>
        public string Text => Encoding.Unicode.GetString(Value[..(NulAt(Value, 0) is var end and >= 0 ? end : Value.Length & ~1)]);

        private ReadOnlySpan<byte> Value { get; } = value;
    }
}
