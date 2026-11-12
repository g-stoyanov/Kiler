using System.Reflection.PortableExecutable;

namespace Kiler.App.Tests;

/// <summary>
/// The import tables of a PE32+ image and the forwarders of the export table of a DLL (M3.8b): what Windows loads by name
/// for the exe, which the smoke tests check against what was examined.
/// </summary>
internal static class PeImports
{
    /// <summary>The size of <c>IMAGE_IMPORT_DESCRIPTOR</c>; the RVA of the import lookup table is at 0, of the DLL name at 12, of the address table at 16.</summary>
    private const int ImportEntrySize = 20, ImportLookupOffset = 0, ImportNameOffset = 12, ImportAddressOffset = 16;

    /// <summary>The size of <c>IMAGE_DELAYLOAD_DESCRIPTOR</c>; the RVA of the DLL name is at 4, of the import name table at 16.</summary>
    private const int DelayEntrySize = 32, DelayNameOffset = 4, DelayLookupOffset = 16;

    /// <summary>The bit of a 64-bit thunk that imports by ordinal.</summary>
    private const ulong ByOrdinal = 0x8000_0000_0000_0000;

    /// <summary>The DLLs of the import table and the functions that the image takes from each.</summary>
    public static List<Import> Static(PEReader pe) =>
        Read(pe, pe.PEHeaders.PEHeader!.ImportTableDirectory, ImportEntrySize, ImportNameOffset, ImportLookupOffset, ImportAddressOffset);

    /// <summary>The DLLs of the delay-load table and the functions that the image takes from each.</summary>
    public static List<Import> Delayed(PEReader pe) =>
        Read(pe, pe.PEHeaders.PEHeader!.DelayImportTableDirectory, DelayEntrySize, DelayNameOffset, DelayLookupOffset, DelayLookupOffset);

    /// <summary>
    /// The exports of the DLL at <paramref name="dllPath"/> that forward, each to <c>DLL.Function</c> (the DLL without its
    /// extension), keyed by the function as an import takes it: by its name, and by its ordinal.
    /// </summary>
    public static Dictionary<Function, string> Forwarders(string dllPath)
    {
        using var file = File.OpenRead(dllPath);
        using var pe = new PEReader(file);
        var forwarders = new Dictionary<Function, string>();
        var directory = pe.PEHeaders.PEHeader!.ExportTableDirectory;
        if (directory.Size == 0)
        {
            return forwarders;
        }

        // IMAGE_EXPORT_DIRECTORY, from Base on.
        var header = pe.GetSectionData(directory.RelativeVirtualAddress).GetReader();
        header.Offset = 16;
        var ordinalBase = header.ReadInt32();
        var functionCount = header.ReadInt32();
        var nameCount = header.ReadInt32();
        var functions = header.ReadInt32();
        var names = header.ReadInt32();
        var nameOrdinals = header.ReadInt32();

        string? Forwarder(int index)
        {
            var address = pe.GetSectionData(functions + (index * sizeof(int))).GetReader().ReadInt32();
            var forwards = address >= directory.RelativeVirtualAddress && address < directory.RelativeVirtualAddress + directory.Size;
            return forwards ? ReadName(pe, address) : null;
        }

        for (var index = 0; index < functionCount; index++)
        {
            if (Forwarder(index) is { } target)
            {
                forwarders[new Function(null, ordinalBase + index)] = target;
            }
        }

        for (var i = 0; i < nameCount; i++)
        {
            var index = pe.GetSectionData(nameOrdinals + (i * sizeof(ushort))).GetReader().ReadUInt16();
            if (Forwarder(index) is { } target)
            {
                forwarders[new Function(ReadName(pe, pe.GetSectionData(names + (i * sizeof(int))).GetReader().ReadInt32()), 0)] = target;
            }
        }

        return forwarders;
    }

    private static List<Import> Read(PEReader pe, DirectoryEntry directory, int entrySize, int nameOffset, int lookupOffset, int fallbackOffset)
    {
        var imports = new List<Import>();
        var end = directory.RelativeVirtualAddress + directory.Size;
        for (var entry = directory.RelativeVirtualAddress; entry + entrySize <= end; entry += entrySize)
        {
            var name = Field(pe, entry, nameOffset);
            if (name == 0)
            {
                break;
            }

            var lookup = Field(pe, entry, lookupOffset) is var table and not 0 ? table : Field(pe, entry, fallbackOffset);
            imports.Add(new Import(ReadName(pe, name), ReadFunctions(pe, lookup)));
        }

        return imports;
    }

    private static List<Function> ReadFunctions(PEReader pe, int lookup)
    {
        var functions = new List<Function>();
        for (var thunk = pe.GetSectionData(lookup).GetReader(); thunk.ReadUInt64() is var value and not 0;)
        {
            functions.Add((value & ByOrdinal) != 0
                ? new Function(null, (int)(value & 0xFFFF))
                // IMAGE_IMPORT_BY_NAME: a hint of 2 bytes, then the name.
                : new Function(ReadName(pe, (int)(value & 0x7FFF_FFFF) + sizeof(ushort)), 0));
        }

        return functions;
    }

    private static int Field(PEReader pe, int entry, int offset)
    {
        var reader = pe.GetSectionData(entry).GetReader();
        reader.Offset = offset;
        return reader.ReadInt32();
    }

    private static string ReadName(PEReader pe, int rva)
    {
        var reader = pe.GetSectionData(rva).GetReader();
        return reader.ReadUTF8(reader.IndexOf(0));
    }

    /// <summary>A DLL of an import table and the functions taken from it.</summary>
    public sealed record Import(string Dll, IReadOnlyList<Function> Functions);

    /// <summary>A function taken by its name, or by its ordinal when the name is null.</summary>
    public readonly record struct Function(string? Name, int Ordinal)
    {
        public override string ToString() => Name ?? $"#{Ordinal}";
    }
}
