using Kiler.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kiler.App;

/// <summary>A field of a template as the page gets it (docs/api.md, "Template"): a setting that does not belong to its type is null.</summary>
/// <param name="Key">The key of the field in the record.</param>
/// <param name="Label">The label of the form and the view.</param>
/// <param name="Type">The type of design 6.4, such as <c>secret</c>.</param>
/// <param name="Level">The default level; null when the field has only <paramref name="LevelByEnv"/>.</param>
/// <param name="LevelByEnv">The level by the value of the field <c>env</c>.</param>
/// <param name="Required">Whether a record needs a value.</param>
/// <param name="Filter">Whether the field is a filter of the list.</param>
/// <param name="List">Whether the list shows the field.</param>
/// <param name="Options">The values of a <c>select</c>.</param>
/// <param name="Language">The fixed language of a <c>code</c>.</param>
/// <param name="Dialect">The dialect of <paramref name="Language"/>.</param>
/// <param name="Languages">The languages that a <c>code</c> takes.</param>
/// <param name="RefTemplate">The template of the records that a <c>ref</c> points to.</param>
internal sealed record TemplateFieldResponse(
    string Key,
    string Label,
    string Type,
    int? Level,
    IReadOnlyDictionary<string, int>? LevelByEnv,
    bool Required,
    bool Filter,
    bool List,
    IReadOnlyList<string>? Options,
    string? Language,
    string? Dialect,
    IReadOnlyList<string>? Languages,
    string? RefTemplate);

/// <param name="Id">The id of the template, the name of its file in <c>templates\</c>.</param>
/// <param name="Name">The name of the kind of record.</param>
/// <param name="Icon">The name of the icon; the page takes it from its own set.</param>
/// <param name="Fields">The fields, in the order of the template.</param>
internal sealed record TemplateResponse(string Id, string Name, string Icon, IReadOnlyList<TemplateFieldResponse> Fields);

/// <param name="Id">The id of the file in <c>templates\</c>.</param>
/// <param name="Reason">Why it does not fit; it names a JSON member and never repeats a value of the share.</param>
internal sealed record InvalidTemplate(string Id, string Reason);

/// <param name="Templates">The templates that fit, by name.</param>
/// <param name="Invalid">The files of <c>templates\</c> that do not fit, by id.</param>
internal sealed record TemplatesResponse(IReadOnlyList<TemplateResponse> Templates, IReadOnlyList<InvalidTemplate> Invalid);

/// <param name="Level">The number of the level.</param>
/// <param name="Name">Its name.</param>
internal sealed record LevelName(int Level, string Name);

/// <summary>The levels of <c>levels.json</c>: of <c>GET /api/levels</c>, and of the admin's screen of the levels (<see cref="AdminApi"/>).</summary>
/// <param name="Levels">The levels 0 to the top level.</param>
internal sealed record LevelsResponse(IReadOnlyList<LevelName> Levels)
{
    public static LevelsResponse Of(LevelsFile levels) => new([.. levels.Levels.Select(level => new LevelName(level.Level, level.Name))]);
}

/// <summary>
/// The templates and the names of the levels for every reader (M5.2, docs/api.md): <c>GET /api/templates</c>, for the form,
/// the view and the filters, and <c>GET /api/levels</c>, for the names of the levels in the form and the padlocks. Both need
/// <see cref="Need.Reader"/>, so a guest in guest mode gets them too, and answer from the team in memory, which follows the
/// loads (<see cref="Team"/>). The templates are read only until the exam (decided at the start of M5).
/// </summary>
internal static class TemplatesApi
{
    public static void Map(IEndpointRouteBuilder endpoints, KilerData data)
    {
        endpoints.MapGet("/api/templates", () => Templates(data)).Needs(Need.Reader);
        endpoints.MapGet("/api/levels", () => Levels(data)).Needs(Need.Reader);
    }

    private static Results<Ok<TemplatesResponse>, Conflict<ErrorResponse>> Templates(KilerData data)
    {
        if (data.Files is not { } files)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        var templates = files.Templates.Values
            .Select(file => file.Model)
            .OfType<TemplateFile>()
            .Select(Describe)
            .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(template => template.Id, StringComparer.Ordinal)
            .ToList();
        var invalid = files.Templates
            .Select(pair => pair.Value.IsValid ? null : new InvalidTemplate(pair.Key, pair.Value.Invalid))
            .OfType<InvalidTemplate>()
            .ToList();
        return TypedResults.Ok(new TemplatesResponse(templates, invalid));
    }

    /// <summary>409 also when <c>levels.json</c> cannot be read, as the admin's screen of the levels answers; <c>GET /api/data</c> says why.</summary>
    private static Results<Ok<LevelsResponse>, Conflict<ErrorResponse>> Levels(KilerData data) =>
        data.Files?.Levels?.Model is { } levels
            ? TypedResults.Ok(LevelsResponse.Of(levels))
            : TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));

    private static TemplateResponse Describe(TemplateFile template) => new(
        template.Id,
        template.Name,
        template.Icon,
        [.. template.Fields.Select(field => new TemplateFieldResponse(
            field.Key,
            field.Label,
            TemplateField.NameOf(field.Type),
            field.Level,
            field.LevelByEnv,
            field.Required,
            field.Filter,
            field.List,
            field.Options,
            field.Language,
            field.Dialect,
            field.Languages,
            field.RefTemplate))]);
}
