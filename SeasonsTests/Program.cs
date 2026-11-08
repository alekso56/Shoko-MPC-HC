using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Services;
using ShokoSeasons;

var passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); passed++; }
var scratch = Path.Combine(Path.GetTempPath(), "shoko-seasons-tests-" + Guid.NewGuid().ToString("N"));
var paths = Stub.Make<IApplicationPaths>((_, _) => scratch);
var provider = new Provider();
var http = new HttpClient(provider);
var catalog = new AnimeCatalog(http, paths);
var sonarr = new SonarrGateway(http, () => new("http://sonarr.test", "fixture-key", 4, "/anime"));
var count = 0; var allowed = true; var admin = true; var inLibrary = false;
var series = Stub.Make<IShokoSeries>((m, _) => m.Name switch { "get_LocalID" => 44, "get_LocalEpisodeCounts" => new EpisodeCounts { Episodes = count }, _ => null });
var metadata = Stub.Make<IMetadataService>((m, args) => inLibrary && m.Name is "GetShokoSeriesByAnidbID" or "GetShokoSeriesByID" ? series : null);
var user = Stub.Make<IUser>((m, _) => m.Name switch { "get_LocalID" => 7, "get_IsAdmin" => admin, "IsAllowedToSee" => allowed, _ => null });
var other = Stub.Make<IUser>((m, _) => m.Name switch { "get_LocalID" => 8, "get_IsAdmin" => true, "IsAllowedToSee" => true, _ => null });
var users = Stub.Make<IUserService>((m, _) => m.Name is "GetUserFromHttpContext" or "GetUserByID" ? user : null);
var list = new SeasonList(catalog, sonarr, metadata, users, paths, NullLogger<SeasonList>.Instance);

var maps = AnimeCatalog.ParseMappings("[{\"anilist_id\":1,\"anidb_id\":2,\"tvdb_id\":3,\"season\":{\"tvdb\":2}},{\"anilist_id\":9,\"anidb_id\":8},{\"anilist_id\":9,\"anidb_id\":7}]");
Check(maps[1].Season == 2 && !maps.ContainsKey(9), "exact season mappings retained; ambiguous anime mappings rejected");
if (args.Length > 0) { Check(AnimeCatalog.ParseMappings(File.ReadAllText(args[0])).Count > 1000, "current provider mapping dataset parses successfully"); }
var anime = AnimeCatalog.Parse(JsonNode.Parse("{\"id\":10,\"title\":{\"romaji\":\"Original\"},\"description\":\"<b>Story</b> &amp; <script>bad()</script>\",\"coverImage\":{\"large\":\"http://unsafe.test/x\"}}"));
Check(anime.Title == "Original" && !anime.Description.Contains('<') && anime.Cover == "", "catalog descriptions become plain text and unsafe cover URLs rejected");
var season = await catalog.GetSeason(2026, "FALL", default);
Check(season.Items.Length == 1 && provider.CatalogQueries == 1, "season fetched from provider");
await catalog.GetSeason(2026, "FALL", default);
Check(provider.CatalogQueries == 1, "season switches reuse server cache");
try { await catalog.GetSeason(2026, "BAD", default); throw new Exception("Expected validation"); } catch (SeasonException e) { Check(e.Status == 400, "invalid season rejected"); }

await sonarr.EnsureSeason(100, 2, default);
Check(provider.Series!["seasons"]![0]!["monitored"]!.GetValue<bool>() && provider.Series["seasons"]![1]!["monitored"]!.GetValue<bool>() && !provider.Series["seasons"]![2]!["monitored"]!.GetValue<bool>(), "request changes selected season and preserves other monitoring");
Check(provider.Monitored.SequenceEqual(new[] { 22 }), "only selected season episodes are monitored");
var newProvider = new Provider { Exists = false };
var newSonarr = new SonarrGateway(new HttpClient(newProvider), () => new("http://sonarr.test", "fixture-key", 4, "/anime"));
await newSonarr.EnsureSeason(100, 2, default);
Check(newProvider.Added!["addOptions"].Text("monitor") == "none" && !newProvider.Added["addOptions"]!["searchForMissingEpisodes"]!.GetValue<bool>(), "new series never starts a whole-franchise search");
Check(newProvider.Series!.Array("seasons").Where(s => s!["monitored"]!.GetValue<bool>()).Single().Number("seasonNumber") == 2, "new series monitors only the requested season");
try { await sonarr.EnsureSeason(100, 99, default); throw new Exception("Expected validation"); } catch (SeasonException e) { Check(e.Status == 409, "nonexistent Sonarr season rejected"); }
var entry = await list.Add(user, new(10, 100, 2), default);
Check(entry.SonarrId == 5 && provider.Searches == 1 && provider.LastCommand!.Number("seasonNumber") == 2, "add requests one season search and saves Sonarr ID");
await list.Add(user, new(10, 100, 2), default);
Check(provider.Searches == 1, "duplicate add is idempotent");
Check((await list.Snapshot(other, default)).Entries.Length == 0, "requests isolated per Shoko user");
provider.Queue = JsonNode.Parse("[{\"id\":1,\"seriesId\":5,\"seasonNumber\":2,\"downloadId\":\"pack\",\"size\":100,\"sizeleft\":40,\"status\":\"downloading\"},{\"id\":2,\"seriesId\":5,\"seasonNumber\":2,\"downloadId\":\"pack\",\"size\":100,\"sizeleft\":40,\"status\":\"downloading\"},{\"id\":3,\"seriesId\":5,\"seasonNumber\":1,\"size\":500,\"sizeleft\":500}]")!.AsArray();
await list.Refresh(default);
entry = (await list.Snapshot(user, default)).Entries.Single();
Check(entry.State == "Downloading" && entry.Progress == 60, "progress excludes unrelated seasons and duplicate pack rows");
provider.Queue[0]!["trackedDownloadStatus"] = "warning";
Check(SonarrGateway.ApplyActivity(entry, new JsonArray(provider.Series.DeepClone()), provider.Queue).State == "Needs attention", "Sonarr import warnings surfaced");
provider.Queue = [];
provider.Series["seasons"]![1]!["statistics"] = JsonNode.Parse("{\"episodeFileCount\":2}");
await list.Refresh(default);
Check((await list.Snapshot(user, default)).Entries.Single().State == "Waiting for Shoko", "Sonarr files are not prematurely called ready in Shoko");
inLibrary = true; count = 2; await list.Refresh(default);
var snapshot = await list.Snapshot(user, default);
Check(snapshot.Entries.Single().State == "Ready in Shoko" && snapshot.Notices.Single().Episodes == 2, "identified Shoko episodes produce a ready notification");
await list.Refresh(default);
Check((await list.Snapshot(user, default)).Notices.Length == 1, "unchanged library does not repeat notifications");
count = 3; await list.Refresh(default);
Check((await list.Snapshot(user, default)).Notices.Length == 2, "new arriving episodes generate another notification");
await list.MarkRead(user, default);
var reloaded = new SeasonList(catalog, sonarr, metadata, users, paths, NullLogger<SeasonList>.Instance);
Check((await reloaded.Snapshot(user, default)).Notices.All(n => n.Read), "list and read notifications survive restart");
var searchesBefore = provider.Searches;
try { await list.Add(other, new(10, 100, 2), default); throw new Exception("Expected existing series rejection"); }
catch (SeasonException e) { Check(e.Status == 409 && e.Message.Contains("already in"), "existing Shoko anime rejected before Sonarr mutations"); }
Check(provider.Searches == searchesBefore && (await list.Snapshot(other, default)).Entries.Length == 0, "blocked request adds no list entry and starts no search");
count = 0;
Check((await list.Library(user, [10], default)).Length == 1, "Shoko series is recognized even when it has no playable files");
try { await list.Add(other, new(10, 100, 2), default); throw new Exception("Expected existing empty series rejection"); }
catch (SeasonException e) { Check(e.Status == 409, "existing empty Shoko series also blocks add"); }
count = 3;
allowed = false;
snapshot = await reloaded.Snapshot(user, default);
Check(snapshot.Notices.Length == 0 && snapshot.Entries.Single().ShokoId is null, "revoked series access hides ready links and notifications");
allowed = true; admin = false;
try { await list.Add(user, new(10, 100, 2), default); throw new Exception("Expected forbidden"); } catch (SeasonException e) { Check(e.Status == 403, "non-admin download request rejected"); }
admin = true;

using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
{
    services.AddRouting(); services.AddLogging(); services.AddSingleton(users); services.AddSingleton(metadata);
    services.AddSingleton(catalog); services.AddSingleton(sonarr); services.AddSingleton(list);
    services.AddSingleton<IStartupFilter, WebUiStartupFilter>();
    services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
    services.AddAuthorization();
}).Configure(app =>
{
    app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); Plugin.RegisterServices(app, paths);
    app.Run(async c => { c.Response.ContentType = "text/html"; await c.Response.WriteAsync("<html><head></head><body><div id=\"app-root\"></div></body></html>"); });
})).StartAsync();
var client = host.GetTestClient();
Check((await client.GetAsync(Plugin.BasePath + "/status")).StatusCode == HttpStatusCode.Unauthorized, "anonymous list API rejected");
Check((await client.PostAsJsonAsync(Plugin.BasePath + "/list", new AddRequest(10,100,2))).StatusCode == HttpStatusCode.Unauthorized, "anonymous download API rejected");
client.DefaultRequestHeaders.Add("apikey", "test");
Check((await client.GetAsync(Plugin.BasePath + "/status?ids=10")).IsSuccessStatusCode, "signed-in user can read list and library matches");
Check((await client.GetAsync(Plugin.BasePath + "/status?ids=invalid")).StatusCode == HttpStatusCode.BadRequest, "malformed catalog IDs rejected");
admin = false;
Check((await client.GetAsync(Plugin.BasePath + "/matches?animeId=10")).StatusCode == HttpStatusCode.Forbidden, "non-admin cannot search Sonarr via plugin");
var html = await client.GetStringAsync(Plugin.BasePath + "/");
Check(html.Contains("Seasonal anime") && html.Contains("My list") && !html.Contains("fixture-key"), "embedded page served without credentials");
var request = new HttpRequestMessage(HttpMethod.Get, "/webui/dashboard"); request.Headers.Accept.ParseAdd("text/html");
var response = await client.SendAsync(request);
Check((await response.Content.ReadAsStringAsync()).Contains("shoko-seasons-script"), "seasonal navigation injected into Shoko");
Check((await client.GetStringAsync(Plugin.BasePath + "/app.js")).Contains("refreshStatus"), "embedded application JavaScript served");
Console.WriteLine($"{passed} seasonal backend checks passed.");

public class Provider : HttpMessageHandler
{
    public JsonNode? Series = JsonNode.Parse("{\"id\":5,\"tvdbId\":100,\"title\":\"Test anime\",\"seasons\":[{\"seasonNumber\":1,\"monitored\":true},{\"seasonNumber\":2,\"monitored\":false},{\"seasonNumber\":3,\"monitored\":false}]}");
    public JsonArray Queue = [];
    public bool Exists = true;
    public JsonNode? Added;
    public int[] Monitored = [];
    public int Searches, CatalogQueries;
    public JsonNode? LastCommand;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.PathAndQuery;
        JsonNode result;
        if (request.RequestUri.Host == "graphql.anilist.co")
        {
            var payload = await request.Content!.ReadFromJsonAsync<JsonNode>(ct);
            var anime = JsonNode.Parse("{\"id\":10,\"title\":{\"english\":\"Test anime\"},\"format\":\"TV\",\"genres\":[]}");
            if (payload.Text("query").Contains("Page(")) { CatalogQueries++; result = new JsonObject { ["data"] = new JsonObject { ["Page"] = new JsonObject { ["pageInfo"] = new JsonObject { ["hasNextPage"] = false }, ["media"] = new JsonArray(anime) } } }; }
            else { result = new JsonObject { ["data"] = new JsonObject { ["Media"] = anime } }; }
        }
        else if (request.RequestUri.Host == "raw.githubusercontent.com") { result = JsonNode.Parse("[{\"anilist_id\":10,\"anidb_id\":20,\"tvdb_id\":100,\"season\":{\"tvdb\":2}}]")!; }
        else
        {
            if (request.Headers.GetValues("X-Api-Key").Single() != "fixture-key") throw new Exception("Missing Sonarr authentication");
            if (path == "/api/v3/series" && request.Method == HttpMethod.Post) { Added = await request.Content!.ReadFromJsonAsync<JsonNode>(ct); Exists = true; result = Added!; }
            else if (path.StartsWith("/api/v3/series/lookup")) result = new JsonArray(Series!.DeepClone());
            else if (path.StartsWith("/api/v3/series?") || path == "/api/v3/series") result = Exists ? new JsonArray(Series!.DeepClone()) : new JsonArray();
            else if (path == "/api/v3/series/5" && request.Method == HttpMethod.Put) { Series = await request.Content!.ReadFromJsonAsync<JsonNode>(ct); result = Series!; }
            else if (path.StartsWith("/api/v3/episode?")) result = JsonNode.Parse("[{\"id\":11,\"seasonNumber\":1},{\"id\":22,\"seasonNumber\":2},{\"id\":33,\"seasonNumber\":3}]")!;
            else if (path == "/api/v3/episode/monitor") { var data = await request.Content!.ReadFromJsonAsync<JsonNode>(ct); Monitored = data.Array("episodeIds").Select(x => x!.GetValue<int>()).ToArray(); result = new JsonObject(); }
            else if (path == "/api/v3/command") { Searches++; LastCommand = await request.Content!.ReadFromJsonAsync<JsonNode>(ct); result = new JsonObject(); }
            else if (path.StartsWith("/api/v3/queue?")) result = new JsonObject { ["totalRecords"] = Queue.Count, ["records"] = Queue.DeepClone() };
            else throw new Exception("Unexpected provider route " + path);
        }
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
    }
}
public class Stub : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Callback = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Callback(method!, args);
    public static T Make<T>(Func<MethodInfo, object?[]?, object?> callback) where T : class
    { var stub = Create<T, Stub>(); ((Stub)(object)stub).Callback = callback; return stub; }
}
public class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers["apikey"] == "test"
        ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test")), "test"))
        : AuthenticateResult.NoResult());
}
