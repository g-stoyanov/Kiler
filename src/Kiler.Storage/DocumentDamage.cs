namespace Kiler.Storage;

/// <summary>Why a document file is damaged (M2.2). A damaged file is reported (M2.8), not thrown.</summary>
public enum DocumentDamage
{
    /// <summary>Larger than <see cref="DocumentFormat.MaxSize"/>. Such a file is not read whole.</summary>
    TooLarge,

    /// <summary>
    /// Not valid JSON in UTF-8: a syntax error, a comment or a trailing comma, more than one value, a name twice in one
    /// object, nesting deeper than <see cref="DocumentFormat.MaxDepth"/>, or a string that does not decode, such as an
    /// escaped lone surrogate.
    /// </summary>
    NotJson,

    /// <summary>Valid JSON, but not an object.</summary>
    NotAnObject,

    /// <summary>
    /// No <c>id</c>, an <c>id</c> that is not a string or not the file name without <c>.json</c>, or a name that is <c>id</c>
    /// in another case, such as <c>ID</c>.
    /// </summary>
    WrongId,

    /// <summary>
    /// No <c>version</c>, one that is not a whole number from 1 to <see cref="int.MaxValue"/>, or a name that is
    /// <c>version</c> in another case.
    /// </summary>
    WrongVersion,
}
