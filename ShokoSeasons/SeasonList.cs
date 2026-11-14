using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Services;

namespace ShokoSeasons;

public sealed class SeasonList : BackgroundService
{
    private readonly AnimeCatalog catalog;
    private readonly SonarrGateway sonarr;
    private readonly IMetadataService metadata;
    private readonly IUserService users;
    private readonly ILogger<SeasonList> logger;
    private readonly ISystemService system;
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private SavedList saved;
    public string? ActivityError { get; private set; }

    public SeasonList(AnimeCatalog catalog, SonarrGateway sonarr, IMetadataService metadata,
        IUserService users, IApplicationPaths paths, ILogger<SeasonList> logger, ISystemService system)
    {
        this.catalog = catalog;
        this.sonarr = sonarr;
        this.metadata = metadata;
        this.users = users;
        this.logger = logger;
        this.system = system;
        path = Path.Combine(paths.DatabasePath, Plugin.PluginId, "requests.json");
        // Corrupt durable state must fail visibly, rather than discard the user's list.
        saved = File.Exists(path) ? JsonSerializer.Deserialize<SavedList>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The seasonal list could not be loaded.") : new([], []);
    }

    public async Task<SavedList> Snapshot(IUser user, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            bool Visible(int id) => metadata.GetShokoSeriesByID(id) is { } series && user.IsAllowedToSee(series);
            return new(saved.Entries.Where(x => x.UserId == user.LocalID).Select(x => x.ShokoId is { } id && !Visible(id)
                ? x with { ShokoId = null, AvailableEpisodes = 0, State = "Unavailable in Shoko", Message = null } : x).ToArray(),
                saved.Notices.Where(x => x.UserId == user.LocalID && Visible(x.ShokoId)).ToArray());
        }
        finally { gate.Release(); }
    }

    public async Task<object[]> Library(IUser user, int[] animeIds, CancellationToken ct)
    {
        var mappings = await catalog.GetMappings(ct);
        var result = new List<object>();
        foreach (var id in animeIds.Distinct())
        {
            if (!mappings.TryGetValue(id, out var mapping) || mapping.AnidbId is not > 0) { continue; }
            var series = metadata.GetShokoSeriesByAnidbID(mapping.AnidbId.Value);
            if (series is null || !user.IsAllowedToSee(series)) { continue; }
            var count = series.LocalEpisodeCounts.Episodes + series.LocalEpisodeCounts.Specials;
            result.Add(new { animeId = id, shokoId = series.LocalID, availableEpisodes = count });
        }
        return result.ToArray();
    }

    public async Task<ListEntry> Add(IUser user, AddRequest request, CancellationToken ct)
    {
        if (!user.IsAdmin) { throw new SeasonException("Only Shoko administrators can request downloads.", 403); }
        if (request.TvdbId <= 0 || request.Season < 0 || request.Season > 1000) { throw new SeasonException("Choose a Sonarr series and season.", 400); }
        var anime = await catalog.GetAnime(request.AnimeId, ct);
        if (anime.Format is "MOVIE" or "MUSIC") { throw new SeasonException("Sonarr supports series. This title cannot be requested through Sonarr.", 400); }
        var mapping = (await catalog.GetMappings(ct)).GetValueOrDefault(anime.Id);
        RejectExistingSeries(mapping, user);
        var candidates = await sonarr.Find("tvdb:" + request.TvdbId, null, ct);
        if (!candidates.Any(x => x.TvdbId == request.TvdbId && x.Seasons.Contains(request.Season)))
        {
            throw new SeasonException("The selected series or season is unavailable in Sonarr.", 409);
        }
        await gate.WaitAsync(ct);
        try
        {
            RejectExistingSeries(mapping, user);
            var old = saved.Entries.FirstOrDefault(x => x.UserId == user.LocalID && x.AnimeId == anime.Id);
            if (old is not null && old.State is not ("Search failed" or "Adding" or "Removed from Sonarr")) { return old; }
            if (old is not null && (old.TvdbId != request.TvdbId || old.Season != request.Season))
            {
                throw new SeasonException("This title already has a request for a different Sonarr season.", 409);
            }
            if (old is null && saved.Entries.Count(x => x.UserId == user.LocalID) >= 1000) { throw new SeasonException("Your seasonal list is full.", 409); }
            var entry = old ?? new(user.LocalID, anime.Id, anime.Title, anime.Cover, mapping?.AnidbId, request.TvdbId, request.Season, null, "Adding");
            entry = entry with { State = "Adding", Message = "Sending request to Sonarr.", UpdatedAt = DateTimeOffset.UtcNow };
            await StoreEntry(entry, ct);
            try
            {
                // After persisting intent, finish even if the browser closes. The next retry can reconcile Sonarr by TVDB ID.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
                var sonarrId = await sonarr.EnsureSeason(entry.TvdbId, entry.Season, timeout.Token);
                entry = entry with { SonarrId = sonarrId };
                await StoreEntry(entry, CancellationToken.None);
                await sonarr.Search(sonarrId, entry.Season, timeout.Token);
                entry = entry with { State = "Waiting for release", Message = "Added to Sonarr. Searching for this season.", UpdatedAt = DateTimeOffset.UtcNow };
            }
            catch (Exception e) when (e is HttpRequestException or SeasonException or TaskCanceledException)
            {
                entry = entry with { State = "Search failed", Message = "Sonarr could not finish the request. Check its Activity page, then retry.", UpdatedAt = DateTimeOffset.UtcNow };
            }
            await StoreEntry(entry, CancellationToken.None);
            return entry;
        }
        finally { gate.Release(); }
    }

    private void RejectExistingSeries(AnimeMapping? mapping, IUser user)
    {
        if (mapping?.AnidbId is > 0 && metadata.GetShokoSeriesByAnidbID(mapping.AnidbId.Value) is { } series)
        {
            throw new SeasonException(user.IsAllowedToSee(series)
                ? "This anime is already in your Shoko library. Open its episodes from the catalog."
                : "This anime is unavailable to your Shoko account.", 409);
        }
    }

    public async Task MarkRead(IUser user, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await Save(saved with { Notices = saved.Notices.Select(n => n.UserId == user.LocalID ? n with { Read = true } : n).ToArray() }, ct); }
        finally { gate.Release(); }
    }

    private Task StoreEntry(ListEntry entry, CancellationToken ct) => Save(saved with
    {
        Entries = saved.Entries.Where(x => x.UserId != entry.UserId || x.AnimeId != entry.AnimeId).Append(entry).ToArray()
    }, ct);

    private async Task Save(SavedList value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(value), ct);
        File.Move(path + ".tmp", path, true);
        saved = value;
    }

    public async Task Refresh(CancellationToken ct)
    {
        // Hosted services start before Shoko initializes its repositories. Persisted requests must wait.
        if (!system.IsStarted) { return; }
        await gate.WaitAsync(ct);
        try
        {
            if (saved.Entries.Length == 0) { return; }
            var activity = await sonarr.GetActivity(ct);
            var mappings = await catalog.GetMappings(ct);
            var entries = new List<ListEntry>();
            var notices = saved.Notices.ToList();
            foreach (var old in saved.Entries)
            {
                var user = users.GetUserByID(old.UserId);
                if (user is null || !user.IsAdmin) { entries.Add(old); continue; }
                var entry = SonarrGateway.ApplyActivity(old, activity.Series, activity.Queue);
                var anidb = old.AnidbId ?? mappings.GetValueOrDefault(old.AnimeId)?.AnidbId;
                var series = anidb is > 0 ? metadata.GetShokoSeriesByAnidbID(anidb.Value) : null;
                var count = series is not null && user.IsAllowedToSee(series) ? series.LocalEpisodeCounts.Episodes + series.LocalEpisodeCounts.Specials : 0;
                entry = entry with { AnidbId = anidb, AvailableEpisodes = count, ShokoId = count > 0 ? series!.LocalID : null };
                if (count > 0)
                {
                    if (entry.State is "Waiting for Shoko" or "Waiting for release")
                    {
                        entry = entry with { State = "Ready in Shoko", Message = $"{count} episode(s) available. Sonarr continues monitoring this season." };
                    }
                    if (count > old.AvailableEpisodes)
                    {
                        notices.Add(new(Guid.NewGuid().ToString("N"), old.UserId, old.AnimeId, old.Title, series!.LocalID, count, DateTimeOffset.UtcNow));
                    }
                }
                else if (anidb is not > 0)
                {
                    entry = entry with { Message = "Waiting for an AniDB mapping to confirm availability in Shoko. Download progress is still shown." };
                }
                if (entry != old) { entry = entry with { UpdatedAt = DateTimeOffset.UtcNow }; }
                entries.Add(entry);
            }
            var next = new SavedList(entries.ToArray(), notices.GroupBy(x => x.UserId).SelectMany(g => g.OrderByDescending(n => n.CreatedAt).Take(100)).ToArray());
            if (!next.Entries.SequenceEqual(saved.Entries) || !next.Notices.SequenceEqual(saved.Notices)) { await Save(next, ct); }
            ActivityError = null;
        }
        finally { gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Refresh(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            // A plugin polling failure must never stop the host. Keep the last saved snapshot and retry.
            catch (Exception e)
            {
                ActivityError = "Live status is unavailable. The last known status is shown; retrying automatically.";
                logger.LogWarning("Seasonal activity refresh failed ({ErrorType}).", e.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
