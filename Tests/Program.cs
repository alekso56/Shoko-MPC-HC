using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Services;
using Shoko.Abstractions.User.Update;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Services;
using ShokoMpc;
using ShokoMpc.Casting;

var passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); passed++; }
var clock = new FakeClock();
var tokens = new PlaybackTokens(clock);
var launch = tokens.IssueLaunch(1, [9]);
Check(tokens.Redeem(launch)?.FileIds[0] == 9 && tokens.Redeem(launch) is null, "launch is single-use");
launch = tokens.IssueLaunch(1, [9]); clock.Now = clock.Now.AddSeconds(91);
Check(tokens.Redeem(launch) is null, "launch expires after ninety seconds");
var streamToken = tokens.IssueStream(1, 9); clock.Now = clock.Now.AddHours(13);
Check(tokens.FindStream(streamToken) is null, "stream session expires");
Check(tokens.Redeem("../invalid") is null && tokens.FindStream("invalid") is null, "malformed tokens rejected");

bool permitted = true;
var savedWatches = new List<(int File, int User)>();
var bookmarks = new Dictionary<(int File, int User), double>();
var positionWrites = 0;
var series = Stub.Make<IShokoSeries>((_, _) => null);
var user = Stub.Make<IUser>((m, _) => m.Name switch { "get_LocalID" => 7, "IsAllowedToSee" => permitted, _ => null });
var video = Stub.Make<IVideo>((m, _) => m.Name switch {
    "get_LocalID" => 9, "get_EarliestKnownName" => "[Group] Show - 01 日本語.mkv", "get_Series" => (IReadOnlyList<IShokoSeries>)new[] { series },
    "GetStream" => new MemoryStream(Encoding.UTF8.GetBytes("0123456789")), _ => null });
var users = Stub.Make<IUserService>((m, _) => m.Name is "GetUserFromHttpContext" or "GetUserByID" ? user : null);
var secondVideo = Stub.Make<IVideo>((m, _) => m.Name switch { "get_LocalID" => 10, "get_Series" => (IReadOnlyList<IShokoSeries>)new[] { series }, _ => null });
var videos = Stub.Make<IVideoService>((m, a) => m.Name == "GetVideoByID" ? (int)a![0]! switch { 9 => video, 10 => secondVideo, _ => null } : null);
var userData = Stub.Make<IUserDataService>((m, a) => {
    if (m.Name == "GetVideoUserData")
    {
        var key = (((IVideo)a![0]!).LocalID, ((IUser)a[1]!).LocalID);
        return Stub.Make<IVideoUserData>((property, _) => property.Name == "get_ProgressPosition" ? TimeSpan.FromSeconds(bookmarks.GetValueOrDefault(key)) : null);
    }
    if (m.Name == "SaveVideoUserData")
    {
        var update = (VideoUserDataUpdate)a![2]!;
        if (update.ApplyCompletionThreshold != false) throw new Exception("Progress must preserve the plugin's ninety-percent rule.");
        bookmarks[(((IVideo)a[0]!).LocalID, ((IUser)a[1]!).LocalID)] = update.ProgressPosition!.Value.TotalSeconds;
        positionWrites++;
        return Task.FromResult<IVideoUserData>(null!);
    }
    if (m.Name != "SetVideoWatchedStatus") return null;
    savedWatches.Add((((IVideo)a![0]!).LocalID, ((IUser)a[1]!).LocalID));
    return Task.FromResult<IVideoUserData>(null!);
});
// TVCAST-001 contract v1: the cast agent and its settings are replaced by fakes, and every log line is captured.
var castAgent = new FakeCastAgent();
var castSettings = new FakeCastSettings();
var capturedLogs = new CapturedLogs();
using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => {
    services.AddRouting(); services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(capturedLogs));
    services.AddSingleton(users); services.AddSingleton(videos); services.AddSingleton(userData);
    services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
    services.AddAuthorization(); Plugin.RegisterServices(services, null!);
    services.AddSingleton<ICastAgent>(castAgent); services.AddSingleton<ICastSettingsSource>(castSettings);
}).Configure(app => {
    app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); Plugin.RegisterServices(app, null!);
    app.Run(async c => { c.Response.ContentType = c.Request.Path.StartsWithSegments("/api") ? "application/json" : "text/html";
        await c.Response.WriteAsync(c.Request.Path.StartsWithSegments("/api") ? "{}" : "<html><head></head><body><div id=\"app-root\"></div></body></html>"); });
})).StartAsync();
var client = host.GetTestClient();
var prefix = Plugin.BasePath;
var response = await client.PostAsJsonAsync(prefix + "/launch", new { fileIds = new[] { 9 } });
Check(response.StatusCode == HttpStatusCode.Unauthorized, "anonymous launch denied");
client.DefaultRequestHeaders.Add("apikey", "test");
response = await client.PostAsJsonAsync(prefix + "/launch", new { fileIds = new[] { 999 } });
Check(response.StatusCode == HttpStatusCode.NotFound, "unknown file denied");
permitted = false;
response = await client.PostAsJsonAsync(prefix + "/launch", new { fileIds = new[] { 9 } });
Check(response.StatusCode == HttpStatusCode.NotFound, "restricted file denied");
permitted = true;
response = await client.PostAsJsonAsync(prefix + "/launch", new { fileIds = new[] { 9 } });
Check(response.IsSuccessStatusCode, "authenticated playback ticket issued");
var ticket = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString();
client.DefaultRequestHeaders.Remove("apikey");
response = await client.PostAsync(prefix + "/resolve/" + ticket, new StringContent(""));
var json = await response.Content.ReadFromJsonAsync<JsonElement>();
var streamPath = json.GetProperty("paths")[0].GetString();
var namedPath = json.GetProperty("namedPaths")[0].GetString();
var completionPath = json.GetProperty("completionPath").GetString();
Check(savedWatches.Count == 0, "opening an episode does not mark it watched");
Check((await client.GetAsync(completionPath + "/0")).StatusCode != HttpStatusCode.OK, "completion requires POST");
Check((await client.PostAsync(prefix + "/complete/" + streamPath!.Split('/').Last() + "/0", null)).StatusCode == HttpStatusCode.Gone, "stream token cannot change watched status");
Check((await client.PostAsync(completionPath + "/99", null)).StatusCode == HttpStatusCode.BadRequest, "out of range episode part rejected");
permitted = false;
Check((await client.PostAsync(completionPath + "/0", null)).StatusCode == HttpStatusCode.NotFound && savedWatches.Count == 0, "revoked access cannot mark watched");
permitted = true;
var completionReplies = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.PostAsync(completionPath + "/0", null)));
Check(completionReplies.All(r => r.IsSuccessStatusCode) && savedWatches.SequenceEqual(new[] { (9, 7) }), "completion is idempotent and uses launching user's account");
var sessionStore = host.Services.GetRequiredService<CompletionSessions>();
var multi = prefix + "/complete/" + sessionStore.Issue(7, [9, 10]);
await client.PostAsync(multi + "/0", null);
Check(savedWatches.Count == 1, "multipart episode waits for all parts");
await client.PostAsync(multi + "/1", null);
Check(savedWatches.Skip(1).SequenceEqual(new[] { (9, 7), (10, 7) }), "multipart completion propagates watched status through Shoko service");
var expiring = new CompletionSessions(clock);
var expired = expiring.Issue(7, [9]); clock.Now = clock.Now.AddHours(13);
Check(expiring.Find(expired) is null, "watched write capability expires");
var progressToken = sessionStore.Issue(7, [9]);
var progressPath = prefix + "/progress/" + progressToken + "/0";
async Task<HttpResponseMessage> Report(string path, long sequence, double seconds, double duration = 100)
    => await client.PostAsJsonAsync(path, new { sequence, seconds, duration });
Check((await Report(progressPath, 1, 35)).IsSuccessStatusCode && bookmarks[(9, 7)] == 35 && savedWatches.Count == 3, "partial progress saved for launching account without watched change");
Check((await Report(progressPath, 2, 15)).IsSuccessStatusCode && bookmarks[(9, 7)] == 15, "seeking backwards updates resume bookmark");
var writesBefore = positionWrites;
await Report(progressPath, 1, 35); await Report(progressPath, 2, 15);
Check(bookmarks[(9, 7)] == 15 && positionWrites == writesBefore, "duplicate and out-of-order progress cannot overwrite newer bookmark");
Check((await Report(progressPath, 3, -1)).StatusCode == HttpStatusCode.BadRequest &&
    (await Report(progressPath, 3, 1010)).StatusCode == HttpStatusCode.BadRequest &&
    (await Report(progressPath, 3, 1, 0)).StatusCode == HttpStatusCode.BadRequest, "invalid progress rejected");
Check((await Report(prefix + "/progress/" + streamPath!.Split('/').Last() + "/0", 1, 10)).StatusCode == HttpStatusCode.Gone, "media token cannot write resume data");
Check((await Report(prefix + "/progress/" + progressToken + "/20", 1, 10)).StatusCode == HttpStatusCode.BadRequest, "progress part bounds checked");
permitted = false;
Check((await Report(progressPath, 3, 40)).StatusCode == HttpStatusCode.NotFound && bookmarks[(9, 7)] == 15, "revoked access cannot update bookmark");
permitted = true;
var newTicket = host.Services.GetRequiredService<PlaybackTokens>().IssueLaunch(7, [9]);
var reopened = await client.PostAsync(prefix + "/resolve/" + newTicket, null);
var resumeJson = await reopened.Content.ReadFromJsonAsync<JsonElement>();
Check(resumeJson.GetProperty("resumeSeconds")[0].GetDouble() == 15, "reopening returns saved resume seconds");
var reopenedProgress = resumeJson.GetProperty("progressPath").GetString() + "/0";
Check((await Report(progressPath, 4, 50)).StatusCode == HttpStatusCode.Conflict && bookmarks[(9, 7)] == 15, "older launch cannot overwrite newer session's bookmark");
await Report(reopenedProgress, 1, 90);
Check(bookmarks[(9, 7)] == 0 && savedWatches.Count == 4, "ninety percent clears bookmark and marks watched");
await Report(reopenedProgress, 2, 10);
Check(bookmarks[(9, 7)] == 0 && savedWatches.Count == 4, "late progress cannot recreate a completed bookmark");
var multipartProgress = prefix + "/progress/" + sessionStore.Issue(7, [9, 10]);
await Report(multipartProgress + "/0", 1, 90);
await Report(multipartProgress + "/1", 1, 20);
Check(bookmarks[(9, 7)] == 0 && bookmarks[(10, 7)] == 20 && savedWatches.Count == 4, "multipart files retain independent bookmarks without premature completion");
await Report(multipartProgress + "/1", 2, 90);
Check(bookmarks[(10, 7)] == 0 && savedWatches.Count == 6, "multipart completion clears every bookmark");
Check(namedPath == streamPath + "/" + Uri.EscapeDataString("[Group] Show - 01 日本語.mkv"), "stream URL preserves readable filename with Unicode");
Check(response.IsSuccessStatusCode && !json.ToString().Contains("apikey"), "resolve without sending permanent credentials to Windows");
Check((await client.PostAsync(prefix + "/resolve/" + ticket, new StringContent(""))).StatusCode == HttpStatusCode.Gone, "redeeming twice rejected over HTTP");
var range = new HttpRequestMessage(HttpMethod.Get, streamPath); range.Headers.Range = new(2, 5);
response = await client.SendAsync(range);
Check(response.StatusCode == HttpStatusCode.PartialContent && await response.Content.ReadAsStringAsync() == "2345", "media byte-range seeking works");
response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, streamPath));
Check(response.Content.Headers.ContentLength == 10 && (await response.Content.ReadAsByteArrayAsync()).Length == 0, "HEAD returns size without media bytes");
var namedRange = new HttpRequestMessage(HttpMethod.Get, namedPath); namedRange.Headers.Range = new(2, 5);
response = await client.SendAsync(namedRange);
Check(response.StatusCode == HttpStatusCode.PartialContent && await response.Content.ReadAsStringAsync() == "2345", "named stream supports byte-range seeking");
response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, namedPath));
Check(response.Content.Headers.ContentLength == 10, "named stream supports HEAD");
var invalidPath = namedPath!.Replace(streamPath!.Split('/').Last(), new string('0', 64));
Check((await client.GetAsync(invalidPath)).StatusCode == HttpStatusCode.Gone, "filename alone cannot authorize playback");
var unsafeVideo = Stub.Make<IVideo>((m, _) => m.Name switch { "get_LocalID" => 9, "get_EarliestKnownName" => "C:\\library\\Show: 01\r\n%2F.mkv", _ => null });
Check(PlaybackEndpoints.DisplayFileName(unsafeVideo) == "Show_ 01___2F.mkv", "display names remove directories and unsafe characters");
permitted = false;
Check((await client.GetAsync(streamPath)).StatusCode == HttpStatusCode.NotFound, "permissions rechecked after launch");
Check((await client.GetAsync(namedPath)).StatusCode == HttpStatusCode.NotFound, "named streams recheck permissions");
var document = new HttpRequestMessage(HttpMethod.Get, "/webui/collection/series/216/episodes"); document.Headers.Accept.ParseAdd("text/html");
response = await client.SendAsync(document);
var html = await response.Content.ReadAsStringAsync();
Check(html.Contains("id=\"shoko-mpc-script\"") && Encoding.UTF8.GetByteCount(html) == response.Content.Headers.ContentLength, "Web UI injection and content length");
Check(await client.GetStringAsync("/api/test") == "{}", "API responses not modified");
Check((await client.GetStringAsync(prefix + "/dashboard")).Contains("Download Windows Setup"), "setup page served");
Check((await client.GetByteArrayAsync(prefix + "/Windows-Setup.zip")).Take(2).SequenceEqual(new byte[] { 80, 75 }), "Windows setup archive embedded");
// ---- TVCAST-001 acceptance contract v1 (plugin side). Implementation agents must not edit below this line. ----
var hexToken = new System.Text.RegularExpressions.Regex("[0-9a-f]{64}");
const string agentSecret = "agent-secret-for-tests-0123456789abcdef";
castSettings.Value = new CastSettings(new Uri("http://cast-agent.test:17300/"), agentSecret, new Uri("http://shoko-lan.test:8111/"));
permitted = true; bookmarks[(9, 7)] = 15;
async Task<HttpResponseMessage> CastCall(HttpMethod method, object? body = null, bool signedIn = true)
{
    var message = new HttpRequestMessage(method, prefix + "/cast");
    if (body is not null) message.Content = JsonContent.Create(body);
    if (signedIn) message.Headers.Add("apikey", "test");
    return await client.SendAsync(message);
}
async Task<string?> ErrorOf(HttpResponseMessage reply) => (await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

// R3: same authentication and validation as /launch; nothing reaches the agent on a rejected request.
Check((await CastCall(HttpMethod.Post, new { fileIds = new[] { 9 } }, signedIn: false)).StatusCode == HttpStatusCode.Unauthorized
    && (await CastCall(HttpMethod.Get, signedIn: false)).StatusCode == HttpStatusCode.Unauthorized
    && (await CastCall(HttpMethod.Delete, signedIn: false)).StatusCode == HttpStatusCode.Unauthorized, "cast-requires-auth");
response = await CastCall(HttpMethod.Post, new { fileIds = Array.Empty<int>() });
var seventeen = await CastCall(HttpMethod.Post, new { fileIds = Enumerable.Range(1, 17).ToArray() });
var negative = await CastCall(HttpMethod.Post, new { fileIds = new[] { -9 } });
Check(response.StatusCode == HttpStatusCode.BadRequest && seventeen.StatusCode == HttpStatusCode.BadRequest && negative.StatusCode == HttpStatusCode.BadRequest
    && await ErrorOf(response) == "Choose between one and sixteen files.", "cast-rejects-0-and-17-files");
var garbage = new HttpRequestMessage(HttpMethod.Post, prefix + "/cast") { Content = new StringContent("{not json", Encoding.UTF8, "application/json") };
garbage.Headers.Add("apikey", "test");
response = await client.SendAsync(garbage);
Check(response.StatusCode == HttpStatusCode.BadRequest && await ErrorOf(response) == "Invalid cast request.", "cast-rejects-invalid-json");
var oversized = new HttpRequestMessage(HttpMethod.Post, prefix + "/cast") { Content = new StringContent("{\"fileIds\":[9]," + new string(' ', 5000) + "}", Encoding.UTF8, "application/json") };
oversized.Headers.Add("apikey", "test");
Check((await client.SendAsync(oversized)).StatusCode == HttpStatusCode.RequestEntityTooLarge, "cast-rejects-body-over-4k");
permitted = false;
response = await CastCall(HttpMethod.Post, new { fileIds = new[] { 9 } });
Check(response.StatusCode == HttpStatusCode.NotFound && await ErrorOf(response) == "This file is unavailable to your Shoko account.", "cast-rejects-hidden-series");
permitted = true;
Check((await CastCall(HttpMethod.Post, new { fileIds = new[] { 999 } })).StatusCode == HttpStatusCode.NotFound, "cast-rejects-unknown-file");
// R18: multi-part episodes are refused before the agent is contacted.
response = await CastCall(HttpMethod.Post, new { fileIds = new[] { 9, 10 } });
Check(response.StatusCode == HttpStatusCode.BadRequest && await ErrorOf(response) == "Multi-part episodes can't play on the TV yet.", "cast-multipart-rejected");
// R4: an unconfigured plugin answers 503 without contacting the agent.
castSettings.Value = null;
response = await CastCall(HttpMethod.Post, new { fileIds = new[] { 9 } });
Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && await ErrorOf(response) == "Set up TV casting in the plugin settings.", "cast-not-configured-503");
castSettings.Value = new CastSettings(new Uri("http://cast-agent.test:17300/"), agentSecret, new Uri("http://shoko-lan.test:8111/"));
Check(castAgent.Started.Count == 0 && castAgent.StopCalls == 0 && castAgent.StatusCalls == 0, "cast-no-agent-call-on-reject");

// R4: a valid request sends LAN URLs with fresh capabilities and the saved resume position.
response = await CastCall(HttpMethod.Post, new { fileIds = new[] { 9 } });
var castBody = await response.Content.ReadAsStringAsync();
Check(response.StatusCode == HttpStatusCode.Accepted && JsonDocument.Parse(castBody).RootElement.GetProperty("state").GetString() == "connecting", "cast-accepted-202-connecting");
Check(!hexToken.IsMatch(castBody) && !castBody.Contains(agentSecret) && !castBody.Contains("shoko-lan.test"), "cast-response-has-no-token");
var sent = castAgent.Started.Single();
const string lanPrefix = "http://shoko-lan.test:8111/api/plugin/ShokoMpc";
var mediaMatch = System.Text.RegularExpressions.Regex.Match(sent.MediaUrl, "^" + System.Text.RegularExpressions.Regex.Escape(lanPrefix) + "/stream/([0-9a-f]{64})/(.+)$");
var progressMatch = System.Text.RegularExpressions.Regex.Match(sent.ProgressUrl, "^" + System.Text.RegularExpressions.Regex.Escape(lanPrefix) + "/progress/([0-9a-f]{64})/0$");
var completeMatch = System.Text.RegularExpressions.Regex.Match(sent.CompleteUrl, "^" + System.Text.RegularExpressions.Regex.Escape(lanPrefix) + "/complete/([0-9a-f]{64})/0$");
Check(mediaMatch.Success && progressMatch.Success && completeMatch.Success && progressMatch.Groups[1].Value == completeMatch.Groups[1].Value
    && mediaMatch.Groups[1].Value != progressMatch.Groups[1].Value, "cast-sends-lan-url-and-resume: URL shapes");
Check(sent.StartSeconds == 15 && sent.Title == "[Group] Show - 01 日本語.mkv"
    && mediaMatch.Groups[2].Value == Uri.EscapeDataString("[Group] Show - 01 日本語.mkv"), "cast-sends-lan-url-and-resume: resume and title");
var castRange = new HttpRequestMessage(HttpMethod.Get, new Uri(sent.MediaUrl).AbsolutePath); castRange.Headers.Range = new(2, 5);
response = await client.SendAsync(castRange);
Check(response.StatusCode == HttpStatusCode.PartialContent && await response.Content.ReadAsStringAsync() == "2345", "cast media capability streams with byte ranges");
Check((await Report(new Uri(sent.ProgressUrl).AbsolutePath, 1, 40)).IsSuccessStatusCode && bookmarks[(9, 7)] == 40, "cast progress capability saves the bookmark");
Check((await client.PostAsync(new Uri(sent.CompleteUrl).AbsolutePath, null)).IsSuccessStatusCode, "cast completion capability accepted");

// R4 and R15: agent failures and status are reported in plain words.
castAgent.Failure = new CastAgentUnavailableException("connection refused");
response = await CastCall(HttpMethod.Post, new { fileIds = new[] { 9 } });
var statusWhileDown = await CastCall(HttpMethod.Get);
Check(response.StatusCode == HttpStatusCode.BadGateway && await ErrorOf(response) == "The TV caster is not responding."
    && statusWhileDown.StatusCode == HttpStatusCode.BadGateway, "cast-agent-down-502");
castAgent.Failure = null;
castAgent.Status = new CastStatus(CastState.Playing, "Show - 01", 12.5, null);
var status = await (await CastCall(HttpMethod.Get)).Content.ReadFromJsonAsync<JsonElement>();
Check(status.GetProperty("state").GetString() == "playing" && status.GetProperty("title").GetString() == "Show - 01"
    && status.GetProperty("positionSeconds").GetDouble() == 12.5, "cast-status-proxied");
castAgent.Status = new CastStatus(CastState.Error, null, null, "The TV didn't answer. Put it in screen-mirroring mode and try again.");
status = await (await CastCall(HttpMethod.Get)).Content.ReadFromJsonAsync<JsonElement>();
Check(status.GetProperty("state").GetString() == "error"
    && status.GetProperty("error").GetString() == "The TV didn't answer. Put it in screen-mirroring mode and try again.", "cast-status-error-text");
response = await CastCall(HttpMethod.Delete);
Check(response.StatusCode == HttpStatusCode.Accepted && castAgent.StopCalls == 1, "cast-stop-202");

// R12: the plugin's own log categories never contain capabilities or the agent secret.
var pluginLogs = capturedLogs.TextFor("ShokoMpc");
Check(!hexToken.IsMatch(pluginLogs) && !pluginLogs.Contains(agentSecret), "cast-logs-have-no-token");

// R4 and R11 (client side): CastAgentClient speaks I2 exactly and keeps the secret in the Authorization header.
var agentHandler = new RecordingHandler();
var agentClient = new CastAgentClient(new HttpClient(agentHandler), castSettings, TimeSpan.FromMilliseconds(300));
agentHandler.Reply = _ => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{\"state\":\"connecting\"}", Encoding.UTF8, "application/json") };
var started = await agentClient.StartAsync(new CastRequest("http://m/1", "T", 15, "http://m/p", "http://m/c"), CancellationToken.None);
var startCall = agentHandler.Calls.Single();
var startJson = JsonDocument.Parse(startCall.Body).RootElement;
Check(startCall.Method == HttpMethod.Post && startCall.Uri == "http://cast-agent.test:17300/v1/cast" && startCall.Authorization == "Bearer " + agentSecret
    && started.State == CastState.Connecting, "agent-client-post-v1-cast");
Check(startJson.EnumerateObject().Select(p => p.Name).OrderBy(n => n).SequenceEqual(new[] { "completeUrl", "mediaUrl", "progressUrl", "startSeconds", "title" })
    && startJson.GetProperty("mediaUrl").GetString() == "http://m/1" && startJson.GetProperty("startSeconds").GetDouble() == 15
    && !startCall.Body.Contains(agentSecret), "agent-client-body-fields");
agentHandler.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"state\":\"playing\",\"title\":\"T\",\"positionSeconds\":3}", Encoding.UTF8, "application/json") };
var polled = await agentClient.GetStatusAsync(CancellationToken.None);
Check(agentHandler.Calls.Last().Method == HttpMethod.Get && polled.State == CastState.Playing && polled.PositionSeconds == 3, "agent-client-get-status");
agentHandler.Reply = _ => new HttpResponseMessage(HttpStatusCode.Accepted);
await agentClient.StopAsync(CancellationToken.None);
Check(agentHandler.Calls.Last().Method == HttpMethod.Delete && agentHandler.Calls.Last().Uri == "http://cast-agent.test:17300/v1/cast", "agent-client-delete");
async Task<bool> ThrowsUnavailable(Func<Task> call) { try { await call(); return false; } catch (CastAgentUnavailableException) { return true; } }
agentHandler.Reply = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
var on500 = await ThrowsUnavailable(() => agentClient.GetStatusAsync(CancellationToken.None));
agentHandler.Reply = _ => throw new HttpRequestException("refused");
var onRefused = await ThrowsUnavailable(() => agentClient.GetStatusAsync(CancellationToken.None));
agentHandler.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"state\":\"dancing\"}", Encoding.UTF8, "application/json") };
var onUnknownState = await ThrowsUnavailable(() => agentClient.GetStatusAsync(CancellationToken.None));
agentHandler.Reply = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
var on401 = await ThrowsUnavailable(() => agentClient.StopAsync(CancellationToken.None));
Check(on500 && onRefused && onUnknownState && on401, "agent-client-failures-become-unavailable");
agentHandler.Delay = TimeSpan.FromSeconds(30);
agentHandler.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK);
var timer = System.Diagnostics.Stopwatch.StartNew();
var onTimeout = await ThrowsUnavailable(() => agentClient.GetStatusAsync(CancellationToken.None));
Check(onTimeout && timer.Elapsed < TimeSpan.FromSeconds(3), "agent-client-timeout");
Check(CastAgentClient.DefaultTimeout == TimeSpan.FromSeconds(5), "agent-client-default-timeout-5s");
var registrations = new ServiceCollection(); Plugin.RegisterServices(registrations, null!);
Check(registrations.Any(d => d.ServiceType == typeof(ICastAgent)) && registrations.Any(d => d.ServiceType == typeof(ICastSettingsSource)), "plugin-registers-cast-services");
document = new HttpRequestMessage(HttpMethod.Get, "/webui/collection/series/216/episodes"); document.Headers.Accept.ParseAdd("text/html");
Check((await (await client.SendAsync(document)).Content.ReadAsStringAsync()).Contains("id=\"shoko-mpc-script\""), "regression: Web UI injection unchanged");
Console.WriteLine($"{passed} checks passed. ABI: {typeof(Shoko.Abstractions.Plugin.IPlugin).Assembly.GetName().Version}");

public class FakeClock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
public class FakeCastAgent : ICastAgent
{
    public readonly List<CastRequest> Started = new();
    public int StopCalls, StatusCalls;
    public Exception? Failure;
    public CastStatus Status = new(CastState.Idle, null, null, null);
    public Task<CastStatus> StartAsync(CastRequest request, CancellationToken cancellation)
    { if (Failure is not null) throw Failure; Started.Add(request); return Task.FromResult(new CastStatus(CastState.Connecting, request.Title, null, null)); }
    public Task<CastStatus> GetStatusAsync(CancellationToken cancellation) { StatusCalls++; if (Failure is not null) throw Failure; return Task.FromResult(Status); }
    public Task StopAsync(CancellationToken cancellation) { if (Failure is not null) throw Failure; StopCalls++; return Task.CompletedTask; }
}
public class FakeCastSettings : ICastSettingsSource { public CastSettings? Value; public CastSettings? Read() => Value; }
public class CapturedLogs : ILoggerProvider
{
    private readonly List<(string Category, string Text)> lines = new();
    public ILogger CreateLogger(string category) => new Sink(this, category);
    public string TextFor(string categoryPrefix) { lock (lines) return string.Join("\n", lines.Where(l => l.Category.StartsWith(categoryPrefix)).Select(l => l.Text)); }
    public void Dispose() { }
    private sealed class Sink(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        { lock (owner.lines) owner.lines.Add((category, format(state, error) + " " + error)); }
    }
}
public class RecordingHandler : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, string Uri, string? Authorization, string Body);
    public readonly List<Call> Calls = new();
    public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => new HttpResponseMessage(HttpStatusCode.OK);
    public TimeSpan Delay = TimeSpan.Zero;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellation);
        Calls.Add(new(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellation);
        return Reply(request);
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
        ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "test") }, "test")), "test"))
        : AuthenticateResult.NoResult());
}
