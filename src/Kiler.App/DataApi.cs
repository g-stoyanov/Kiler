using Kiler.Core;

namespace Kiler.App;

/// <param name="State">The state of the data.</param>
/// <param name="Folder">The full path of the data folder; null when none was chosen.</param>
/// <param name="Levels">Why <c>levels.json</c> of the local copy cannot be used; null when it can, and while the copy is not open or watched.</param>
/// <param name="Settings">The same for <c>settings.json</c>.</param>
internal sealed record DataResponse(DataState State, string? Folder, string? Levels, string? Settings);

/// <summary>
/// <c>GET /api/data</c> (M4.2): the state of the data (<see cref="KilerData.State"/>), which the page shows with its reason,
/// and, once the local copy is open, whether its <c>levels.json</c> and <c>settings.json</c> can be used. One that is missing
/// or does not fit is damage that an admin restores, never a new Kiler (decided at the start of M4); the reason never holds a
/// value from the share (<see cref="TeamFile{T}.Invalid"/>).
/// </summary>
internal static class DataApi
{
    /// <summary>The reason of a file of the team that the local copy does not have.</summary>
    public const string MissingFile = "The file is missing.";

    public static void Map(IEndpointRouteBuilder endpoints, KilerData data) =>
        endpoints.MapGet("/api/data", () => TypedResults.Ok(Read(data))).Needs(Need.Nothing);

    private static DataResponse Read(KilerData data)
    {
        var state = data.State;
        var files = state is DataState.Open or DataState.Offline ? data.Team?.Files : null;
        return new(state, data.Folder, Problem(files, files?.Levels), Problem(files, files?.Settings));
    }

    private static string? Problem<T>(TeamFiles? files, TeamFile<T>? file)
        where T : class =>
        files is null ? null : file is null ? MissingFile : file.Invalid;
}
