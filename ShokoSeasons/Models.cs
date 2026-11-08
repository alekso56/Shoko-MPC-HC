using System.Text.Json.Nodes;

namespace ShokoSeasons;

public sealed record Anime(int Id, int? MalId, string Title, string OriginalTitle, string Description,
    string Cover, string Format, int? Episodes, int? Score, string[] Genres, string Studio, string Status);
public sealed record AnimeMapping(int AnilistId, int? AnidbId, int? TvdbId, int? Season);
public sealed record SeasonCatalog(Anime[] Items, bool Stale = false);
public sealed record Candidate(int TvdbId, string Title, int Year, string Overview, int[] Seasons, int? SuggestedSeason);
public sealed record AddRequest(int AnimeId, int TvdbId, int Season);
public sealed record ListEntry(int UserId, int AnimeId, string Title, string Cover, int? AnidbId,
    int TvdbId, int Season, int? SonarrId, string State, double? Progress = null,
    int? ShokoId = null, int AvailableEpisodes = 0, string? Message = null, DateTimeOffset? UpdatedAt = null);
public sealed record ReadyNotice(string Id, int UserId, int AnimeId, string Title, int ShokoId, int Episodes, DateTimeOffset CreatedAt, bool Read = false);
public sealed record SavedList(ListEntry[] Entries, ReadyNotice[] Notices);

public sealed class SeasonException(string message, int status = 502) : Exception(message)
{
    public int Status { get; } = status;
}

// Provider JSON is deliberately kept at the transport boundary.
public static class JsonValueReader
{
    public static string Text(this JsonNode? node, string key) => node?[key]?.GetValue<string>() ?? "";
    public static int? Number(this JsonNode? node, string key) => node?[key] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;
    public static JsonArray Array(this JsonNode? node, string key) => node?[key] as JsonArray ?? [];
}
