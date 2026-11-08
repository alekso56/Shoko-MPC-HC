using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ShokoSeasons;

public sealed class SonarrGateway(HttpClient http, Func<SonarrSettings> settings)
{
    public void CheckConfiguration() => settings();

    private async Task<JsonNode> Send(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var connection = settings();
        using var request = new HttpRequestMessage(method, connection.Url + "/api/v3/" + path);
        request.Headers.Add("X-Api-Key", connection.ApiKey);
        if (body is not null) { request.Content = JsonContent.Create(body); }
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Sonarr errors may include paths and configuration. Return a safe, actionable message.
            throw new SeasonException($"Sonarr returned HTTP {(int)response.StatusCode}. Check its connection and Activity page, then retry.");
        }
        if (response.Content.Headers.ContentLength == 0) { return new JsonObject(); }
        var text = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject();
    }

    public async Task<Candidate[]> Find(string term, int? suggestedSeason, CancellationToken ct)
    {
        var results = await Send(HttpMethod.Get, "series/lookup?term=" + Uri.EscapeDataString(term), null, ct);
        return (results as JsonArray ?? []).Where(x => x.Number("tvdbId") is > 0).Take(20)
            .Select(x => new Candidate(x.Number("tvdbId")!.Value, x.Text("title"), x.Number("year") ?? 0, x.Text("overview"),
                x.Array("seasons").Select(s => s.Number("seasonNumber") ?? -1).Where(s => s >= 0).Order().ToArray(), suggestedSeason)).ToArray();
    }

    public async Task<int> EnsureSeason(int tvdbId, int season, CancellationToken ct)
    {
        var existing = await Send(HttpMethod.Get, "series?tvdbId=" + tvdbId, null, ct);
        var series = (existing as JsonArray)?.FirstOrDefault(x => x.Number("tvdbId") == tvdbId);
        if (series is null)
        {
            var lookup = await Send(HttpMethod.Get, "series/lookup?term=tvdb:" + tvdbId, null, ct);
            series = (lookup as JsonArray)?.FirstOrDefault(x => x.Number("tvdbId") == tvdbId)?.DeepClone();
            if (series is null) { throw new SeasonException("This series is no longer available in Sonarr.", 409); }
            ValidateSeason(series, season);
            var connection = settings();
            series["qualityProfileId"] = connection.QualityProfile;
            series["rootFolderPath"] = connection.RootFolder;
            series["monitored"] = true;
            series["seriesType"] = "anime";
            foreach (var s in series.Array("seasons")) { s!["monitored"] = s.Number("seasonNumber") == season; }
            series["addOptions"] = JsonSerializerNode(new { monitor = "none", searchForMissingEpisodes = false });
            series = await Send(HttpMethod.Post, "series", series, ct);
        }
        ValidateSeason(series, season);
        var id = series.Number("id") ?? throw new SeasonException("Sonarr did not return a series ID.");
        // Preserve every other season, existing file and the user's quality settings.
        series["monitored"] = true;
        foreach (var s in series.Array("seasons").Where(s => s.Number("seasonNumber") == season)) { s!["monitored"] = true; }
        await Send(HttpMethod.Put, "series/" + id, series, ct);
        var episodes = await Send(HttpMethod.Get, "episode?seriesId=" + id, null, ct);
        var ids = (episodes as JsonArray ?? []).Where(x => x.Number("seasonNumber") == season).Select(x => x.Number("id")!.Value).ToArray();
        if (ids.Length > 0) { await Send(HttpMethod.Put, "episode/monitor", new { episodeIds = ids, monitored = true }, ct); }
        return id;
    }

    public async Task Search(int id, int season, CancellationToken ct) =>
        _ = await Send(HttpMethod.Post, "command", new { name = "SeasonSearch", seriesId = id, seasonNumber = season }, ct);

    public async Task<(JsonArray Series, JsonArray Queue)> GetActivity(CancellationToken ct)
    {
        var series = await Send(HttpMethod.Get, "series", null, ct) as JsonArray ?? [];
        var queue = new JsonArray();
        for (var page = 1; page <= 100; page++)
        {
            var data = await Send(HttpMethod.Get, $"queue?page={page}&pageSize=100&includeEpisode=true", null, ct);
            foreach (var item in data.Array("records")) { queue.Add(item?.DeepClone()); }
            if (queue.Count >= (data.Number("totalRecords") ?? queue.Count)) { return (series, queue); }
        }
        throw new SeasonException("Sonarr's queue is too large to read completely.");
    }

    private static void ValidateSeason(JsonNode series, int season)
    {
        if (!series.Array("seasons").Any(x => x.Number("seasonNumber") == season))
        {
            throw new SeasonException("That season is not available in Sonarr yet. Choose another season or try again later.", 409);
        }
    }
    private static JsonNode JsonSerializerNode(object value) => System.Text.Json.JsonSerializer.SerializeToNode(value)!;

    public static ListEntry ApplyActivity(ListEntry entry, JsonArray series, JsonArray queue)
    {
        if (entry.State is "Adding" or "Search failed") { return entry; }
        var current = series.FirstOrDefault(s => s.Number("tvdbId") == entry.TvdbId);
        if (current is null) { return entry with { State = "Removed from Sonarr", Progress = null }; }
        var id = current.Number("id");
        var records = queue.Where(q => q.Number("seriesId") == id &&
            (q.Number("seasonNumber") ?? q?["episode"].Number("seasonNumber")) == entry.Season)
            .GroupBy(q => q.Text("downloadId") is { Length: > 0 } downloadId ? downloadId : q.Number("id").ToString()).Select(g => g.First()!).ToArray();
        if (records.Length > 0)
        {
            var total = records.Sum(q => q["size"]?.GetValue<double>() ?? 0);
            var known = records.All(q => q["sizeleft"] is not null);
            var left = records.Sum(q => q["sizeleft"]?.GetValue<double>() ?? 0);
            double? percent = known && total > 0 ? Math.Round(Math.Clamp((total - left) / total * 100, 0, 100), 1) : null;
            var warning = records.Any(q => q.Text("trackedDownloadStatus") is "warning" or "error" || !string.IsNullOrEmpty(q.Text("errorMessage")));
            var paused = records.Any(q => q.Text("status") == "paused");
            var importing = records.All(q => q.Text("status") == "completed" || q.Text("trackedDownloadState") is "importPending" or "importing");
            return entry with { SonarrId = id, Progress = percent, State = warning ? "Needs attention" : paused ? "Paused" : importing ? "Importing" : "Downloading",
                Message = warning ? "Check Sonarr Activity for the download or import problem." : "Progress of active downloads for this season." };
        }
        var files = current.Array("seasons").FirstOrDefault(s => s.Number("seasonNumber") == entry.Season)?["statistics"].Number("episodeFileCount") ?? 0;
        return entry with { SonarrId = id, Progress = null, State = files > 0 ? "Waiting for Shoko" : "Waiting for release", Message = files > 0
            ? "Sonarr has files. Shoko will show them after import and identification." : "Monitored in Sonarr; waiting for a matching release." };
    }
}
