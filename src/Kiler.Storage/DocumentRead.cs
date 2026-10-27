using System.Diagnostics.CodeAnalysis;

namespace Kiler.Storage;

/// <summary>What reading a document file gave: the document, or why the file is damaged.</summary>
public sealed class DocumentRead
{
    private DocumentRead(Document? document, DocumentDamage? damage, string? owner)
    {
        Document = document;
        Damage = damage;
        Owner = owner;
    }

    /// <summary>The document, when the file is not damaged.</summary>
    public Document? Document { get; }

    /// <summary>Why the file is damaged, when it is.</summary>
    public DocumentDamage? Damage { get; }

    /// <summary>
    /// The owner of the file, read through the handle it was read with, as a SID string (M2.6); <c>null</c> when it cannot
    /// be read. A damaged file has one too.
    /// </summary>
    public string? Owner { get; }

    [MemberNotNullWhen(true, nameof(Damage))]
    [MemberNotNullWhen(false, nameof(Document))]
    public bool IsDamaged => Damage is not null;

    internal static DocumentRead Good(Document document) => new(document, null, null);

    internal static DocumentRead Damaged(DocumentDamage damage) => new(null, damage, null);

    /// <summary>The same read, with the owner of the file it came from.</summary>
    internal DocumentRead WithOwner(string? owner) => new(Document, Damage, owner);
}
