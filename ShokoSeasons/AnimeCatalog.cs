using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Plugin;

namespace ShokoSeasons;

public sealed class AnimeCatalog(HttpClient http, IApplicationPaths paths)
{
    private readonly string cacheDirectory = Path.Combine(paths.CachePath, Plugin.PluginId);
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextQuery;
    private Dictionary<int, AnimeMapping>? mappings;
    private DateTimeOffset mappingsLoaded;
    private const string Fields = "id idMal title{romaji english} description(asHtml:false) coverImage{large} format episodes averageScore genres studios(isMain:true){nodes{name}} status";
    private static readonly string[] Seasons = ["WINTER", "SPRING", "SUMMER", "FALL"];

    public async Task<SeasonCatalog> GetSeason(int year, string season, CancellationToken ct)
    {
        season = season.ToUpperInvariant();
        if (year < 1940 || year > DateTime.UtcNow.Year + 2 || !Seasons.Contains(season))
        {
            throw new SeasonException("Choose a valid year and season.", 400);
        }
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var path = Path.Combine(cacheDirectory, $"{year}-{season}.json");
            Anime[]? cached = null;
            if (File.Exists(path))
            {
                try { cached = JsonSerializer.Deserialize<Anime[]>(await File.ReadAllTextAsync(path, ct)); }
                catch (JsonException) { /* A broken rebuildable cache is replaced below. */ }
            }
            if (cached is not null && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddHours(-6))
            {
                return new(cached);
            }
            try
            {
                var items = new List<Anime>();
                for (var page = 1; page <= 30; page++)
                {
                    var data = await Query("query($year:Int,$season:MediaSeason,$page:Int){Page(page:$page,perPage:50){pageInfo{hasNextPage}media(season:$season,seasonYear:$year,type:ANIME,isAdult:false,sort:POPULARITY_DESC){" + Fields + "}}}", new { year, season, page }, ct);
                    items.AddRange(data?["Page"].Array("media").Select(Parse) ?? []);
                    if (data?["Page"]?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() != true)
                    {
                        var result = items.DistinctBy(x => x.Id).ToArray();
                        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(result), ct);
                        File.Move(path + ".tmp", path, true);
                        return new(result);
                    }
                }
                throw new SeasonException("The catalog returned too many pages. Please try another season.");
            }
            catch (Exception e) when (cached is not null && e is HttpRequestException or SeasonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new(cached!, true);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<Anime> GetAnime(int id, CancellationToken ct)
    {
        if (id <= 0) { throw new SeasonException("Invalid anime.", 400); }
        await gate.WaitAsync(ct);
        try
        {
            var data = await Query("query($id:Int){Media(id:$id,type:ANIME,isAdult:false){" + Fields + "}}", new { id }, ct);
            if (data?["Media"] is not { } media) { throw new SeasonException("Anime not found.", 404); }
            return Parse(media);
        }
        finally { gate.Release(); }
    }

    private async Task<JsonNode?> Query(string query, object variables, CancellationToken ct)
    {
        var delay = nextQuery - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) { await Task.Delay(delay, ct); }
        nextQuery = DateTimeOffset.UtcNow.AddSeconds(2.1);
        using var response = await http.PostAsJsonAsync("https://graphql.anilist.co", new { query, variables }, ct);
        if ((int)response.StatusCode == 429)
        {
            nextQuery = DateTimeOffset.UtcNow.AddSeconds(60);
            throw new SeasonException("The anime catalog is busy. Please try again in a minute.", 503);
        }
        if (!response.IsSuccessStatusCode) { throw new SeasonException("The anime catalog is unavailable. Please try again."); }
        var json = await response.Content.ReadFromJsonAsync<JsonNode>(ct);
        if (json?["errors"] is JsonArray { Count: > 0 }) { throw new SeasonException("The anime catalog could not complete the request."); }
        return json?["data"];
    }

    public async Task<IReadOnlyDictionary<int, AnimeMapping>> GetMappings(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (mappings is not null && mappingsLoaded > DateTimeOffset.UtcNow.AddHours(-24)) { return mappings; }
            Directory.CreateDirectory(cacheDirectory);
            var path = Path.Combine(cacheDirectory, "mappings.json");
            string? json = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
            if (json is null || File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddHours(-24))
            {
                try
                {
                    var downloaded = await http.GetStringAsync("https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-mini.json", ct);
                    _ = ParseMappings(downloaded);
                    await File.WriteAllTextAsync(path + ".tmp", downloaded, ct);
                    File.Move(path + ".tmp", path, true);
                    json = downloaded;
                }
                catch (HttpRequestException) when (json is not null) { }
                catch (TaskCanceledException) when (json is not null && !ct.IsCancellationRequested) { }
            }
            mappings = ParseMappings(json!);
            mappingsLoaded = DateTimeOffset.UtcNow;
            return mappings;
        }
        finally { gate.Release(); }
    }

    public static Dictionary<int, AnimeMapping> ParseMappings(string json) =>
        (JsonNode.Parse(json) as JsonArray ?? throw new JsonException("Invalid mappings."))
        .Where(x => x.Number("anilist_id") is > 0)
        .GroupBy(x => x.Number("anilist_id")!.Value)
        // Ambiguous mappings must not silently select a different anime/season.
        .Where(g => g.Select(x => (x.Number("anidb_id"), x.Number("tvdb_id"), x?["season"].Number("tvdb"))).Distinct().Count() == 1)
        .ToDictionary(g => g.Key, g => new AnimeMapping(g.Key, g.First().Number("anidb_id"), g.First().Number("tvdb_id"), g.First()?["season"].Number("tvdb")));

    public static Anime Parse(JsonNode? media)
    {
        var title = media?["title"].Text("english");
        var original = media?["title"].Text("romaji") ?? "";
        var description = WebUtility.HtmlDecode(Regex.Replace(media.Text("description"), "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromSeconds(1)));
        var cover = media?["coverImage"].Text("large") ?? "";
        if (!Uri.TryCreate(cover, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.EndsWith(".anilist.co", StringComparison.OrdinalIgnoreCase)) { cover = ""; }
        return new(media.Number("id") ?? 0, media.Number("idMal"), string.IsNullOrWhiteSpace(title) ? original : title, original,
            description, cover, media.Text("format"), media.Number("episodes"), media.Number("averageScore"),
            media.Array("genres").Select(x => x!.GetValue<string>()).ToArray(),
            string.Join(", ", media?["studios"].Array("nodes").Select(x => x.Text("name")) ?? []), media.Text("status"));
    }
}
