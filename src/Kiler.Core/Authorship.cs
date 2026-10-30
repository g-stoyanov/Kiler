using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// Who made a record or a stash and when, and who changed it last and when (design 6.1 and 6.6): accounts, and dates in UTC
/// that only Kiler sets. They are the members <c>createdBy</c>, <c>createdAt</c>, <c>updatedBy</c> and <c>updatedAt</c> of
/// the document, outside <c>public</c>, as only the exe writes them (design 5.2).
/// </summary>
public sealed class Authorship
{
    /// <summary>The format of a date in a document: UTC with <c>Z</c>, and the fraction of a second only when it has one.</summary>
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    /// <param name="createdBy">The account that made it, which must pass <see cref="StorageName"/>.</param>
    /// <param name="createdAt">When it was made, in UTC.</param>
    /// <param name="updatedBy">The account that changed it last, which must pass <see cref="StorageName"/>.</param>
    /// <param name="updatedAt">When it was changed last, in UTC.</param>
    /// <exception cref="ArgumentException">An account is not one, or a date is not in UTC.</exception>
    public Authorship(string createdBy, DateTime createdAt, string updatedBy, DateTime updatedAt)
    {
        ArgumentNullException.ThrowIfNull(createdBy);
        ArgumentNullException.ThrowIfNull(updatedBy);
        StorageName.Check(createdBy);
        StorageName.Check(updatedBy);
        if (createdAt.Kind != DateTimeKind.Utc || updatedAt.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("A date is not in UTC.");
        }

        CreatedBy = createdBy;
        CreatedAt = createdAt;
        UpdatedBy = updatedBy;
        UpdatedAt = updatedAt;
    }

    public string CreatedBy { get; }

    public DateTime CreatedAt { get; }

    public string UpdatedBy { get; }

    public DateTime UpdatedAt { get; }

    /// <summary>The members of a document, or why they do not fit; the reason names the member, never its value.</summary>
    internal static bool TryRead(
        string? createdBy,
        string? createdAt,
        string? updatedBy,
        string? updatedAt,
        [NotNullWhen(true)] out Authorship? authorship,
        [NotNullWhen(false)] out string? invalid)
    {
        authorship = null;
        if (!StorageName.IsValid(createdBy))
        {
            invalid = "createdBy is missing or is not the name of an account.";
            return false;
        }

        if (!StorageName.IsValid(updatedBy))
        {
            invalid = "updatedBy is missing or is not the name of an account.";
            return false;
        }

        if (!TryReadDate(createdAt, out var created))
        {
            invalid = "createdAt is missing or is not a date in UTC.";
            return false;
        }

        if (!TryReadDate(updatedAt, out var updated))
        {
            invalid = "updatedAt is missing or is not a date in UTC.";
            return false;
        }

        authorship = new Authorship(createdBy, created, updatedBy, updated);
        invalid = null;
        return true;
    }

    internal static string WriteDate(DateTime date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    private static bool TryReadDate(string? text, out DateTime date) =>
        DateTime.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date);
}
