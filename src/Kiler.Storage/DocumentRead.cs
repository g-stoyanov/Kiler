using System.Diagnostics.CodeAnalysis;

namespace Kiler.Storage;

/// <summary>What reading a document file gave: the document, or why the file is damaged.</summary>
public sealed class DocumentRead
{
    private DocumentRead(Document? document, DocumentDamage? damage)
    {
        Document = document;
        Damage = damage;
    }

    /// <summary>The document, when the file is not damaged.</summary>
    public Document? Document { get; }

    /// <summary>Why the file is damaged, when it is.</summary>
    public DocumentDamage? Damage { get; }

    [MemberNotNullWhen(true, nameof(Damage))]
    [MemberNotNullWhen(false, nameof(Document))]
    public bool IsDamaged => Damage is not null;

    internal static DocumentRead Good(Document document) => new(document, null);

    internal static DocumentRead Damaged(DocumentDamage damage) => new(null, damage);
}
