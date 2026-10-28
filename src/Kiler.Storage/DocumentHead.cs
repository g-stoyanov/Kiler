namespace Kiler.Storage;

/// <summary>
/// What a read of only the head of a document file gave (M2.7c): its <c>version</c>, or 0 and why the file is damaged, with
/// the owner of the file, read through the handle it was read with, or <c>null</c>. The local copy of an admin reads so the
/// private folders of other users, which it keeps only for a restore (M2.9).
/// </summary>
internal readonly record struct DocumentHead(int Version, DocumentDamage? Damage, string? Owner)
{
    /// <summary>The head of a whole read, with its owner.</summary>
    public static DocumentHead Of(DocumentRead read) => new(read.Document?.Version ?? 0, read.Damage, read.Owner);
}
