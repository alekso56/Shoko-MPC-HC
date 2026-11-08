using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Enums;
using Shoko.Abstractions.Plugin.Models;
using Shoko.Abstractions.User.Services;

namespace ShokoSeasons;

public sealed class Plugin : IPlugin, IPluginServiceRegistration, IPluginApplicationRegistration
{
    public const string PluginId = "7b22d3ef-125d-4dca-9006-b7d256c04e03";
    public const string BasePath = "/api/plugin/ShokoSeasons";
    public Guid ID => new(PluginId);
    public string Name => "Shoko Seasons";
    public string Description => "Browse seasonal anime, request seasons through Sonarr and follow downloads into Shoko.";
    public IReadOnlyList<PluginPage> GetPages() => [new() { Name = "Seasonal anime", Url = BasePath + "/", Visibility = PluginPageVisibility.Authenticated }];
    public static void RegisterServices(IServiceCollection services, IApplicationPaths paths)
    {
        services.AddHttpClient("ShokoSeasons", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<SonarrConnection>();
        services.AddSingleton(sp => new AnimeCatalog(sp.GetRequiredService<IHttpClientFactory>().CreateClient("ShokoSeasons"), paths));
        services.AddSingleton(sp => new SonarrGateway(sp.GetRequiredService<IHttpClientFactory>().CreateClient("ShokoSeasons"), sp.GetRequiredService<SonarrConnection>().Read));
        services.AddSingleton<SeasonList>();
        services.AddHostedService(sp => sp.GetRequiredService<SeasonList>());
        services.AddSingleton<IStartupFilter, WebUiStartupFilter>();
    }
    public static void RegisterServices(IApplicationBuilder app, IApplicationPaths paths) => app.UseEndpoints(routes =>
    {
        routes.MapGet(BasePath + "/catalog", Handle).RequireAuthorization();
        routes.MapGet(BasePath + "/status", Handle).RequireAuthorization();
        routes.MapGet(BasePath + "/matches", Handle).RequireAuthorization();
        routes.MapPost(BasePath + "/list", Handle).RequireAuthorization();
        routes.MapPost(BasePath + "/notifications/read", Handle).RequireAuthorization();
    });

    public static async Task Handle(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var user = context.RequestServices.GetRequiredService<IUserService>().GetUserFromHttpContext(context);
        var ct = context.RequestAborted;
        try
        {
            if (user is null) { throw new SeasonException("Sign in to Shoko to browse seasonal anime.", 401); }
            var path = context.Request.Path.Value ?? "";
            var catalog = context.RequestServices.GetRequiredService<AnimeCatalog>();
            var list = context.RequestServices.GetRequiredService<SeasonList>();
            var sonarr = context.RequestServices.GetRequiredService<SonarrGateway>();
            object result;
            if (context.Request.Method == "POST")
            {
                if (string.IsNullOrWhiteSpace(context.Request.Headers["apikey"]) && !context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                { throw new SeasonException("Sign in to Shoko before updating your list.", 401); }
                if (context.Request.ContentLength > 4096) { throw new SeasonException("Request too large.", 413); }
                if (path.EndsWith("/notifications/read")) { await list.MarkRead(user, ct); result = new { success = true }; }
                else
                {
                    var request = await context.Request.ReadFromJsonAsync<AddRequest>(ct) ?? throw new SeasonException("Choose a title and season.", 400);
                    result = await list.Add(user, request, ct);
                }
            }
            else if (path.EndsWith("/catalog"))
            {
                if (!int.TryParse(context.Request.Query["year"], out var year)) { throw new SeasonException("Choose a year.", 400); }
                result = await catalog.GetSeason(year, context.Request.Query["season"].ToString(), ct);
            }
            else if (path.EndsWith("/matches"))
            {
                if (!user.IsAdmin) { throw new SeasonException("Only Shoko administrators can request downloads.", 403); }
                if (!int.TryParse(context.Request.Query["animeId"], out var id)) { throw new SeasonException("Choose an anime.", 400); }
                sonarr.CheckConfiguration();
                var anime = await catalog.GetAnime(id, ct);
                var mapping = (await catalog.GetMappings(ct)).GetValueOrDefault(id);
                var query = context.Request.Query["query"].ToString().Trim();
                if (query.Length > 120) { throw new SeasonException("Search text is too long.", 400); }
                var term = query.Length > 0 ? query : mapping?.TvdbId is > 0 ? "tvdb:" + mapping.TvdbId : anime.Title;
                result = new { candidates = await sonarr.Find(term, query.Length == 0 ? mapping?.Season : null, ct), mapped = query.Length == 0 && mapping?.TvdbId is > 0 };
            }
            else
            {
                var ids = context.Request.Query["ids"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (ids.Length > 1500 || ids.Any(x => !int.TryParse(x, out var id) || id <= 0)) { throw new SeasonException("Invalid catalog IDs.", 400); }
                var snapshot = await list.Snapshot(user, ct);
                string? setupError = null;
                try { sonarr.CheckConfiguration(); } catch (SeasonException e) { setupError = e.Message; }
                result = new { entries = snapshot.Entries, notifications = snapshot.Notices, canAdd = user.IsAdmin && setupError is null,
                    setupError, activityError = list.ActivityError, userId = user.LocalID,
                    library = ids.Length > 0 ? await list.Library(user, ids.Select(int.Parse).ToArray(), ct) : [] };
            }
            await context.Response.WriteAsJsonAsync(result, ct);
        }
        catch (Exception e) when (e is SeasonException or HttpRequestException or System.Text.Json.JsonException or BadHttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) { return; }
            context.Response.StatusCode = e is SeasonException error ? error.Status : e is System.Text.Json.JsonException or BadHttpRequestException ? 400 : 502;
            await context.Response.WriteAsJsonAsync(new { error = e is SeasonException known ? known.Message : "The service could not complete the request. Please try again." }, ct);
        }
    }
}
