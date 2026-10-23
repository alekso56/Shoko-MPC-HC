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
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Services;
using ShokoMpc;

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
var series = Stub.Make<IShokoSeries>((_, _) => null);
var user = Stub.Make<IUser>((m, _) => m.Name switch { "get_LocalID" => 7, "IsAllowedToSee" => permitted, _ => null });
var video = Stub.Make<IVideo>((m, _) => m.Name switch {
    "get_LocalID" => 9, "get_EarliestKnownName" => "[Group] Show - 01 日本語.mkv", "get_Series" => (IReadOnlyList<IShokoSeries>)new[] { series },
    "GetStream" => new MemoryStream(Encoding.UTF8.GetBytes("0123456789")), _ => null });
var users = Stub.Make<IUserService>((m, _) => m.Name is "GetUserFromHttpContext" or "GetUserByID" ? user : null);
var videos = Stub.Make<IVideoService>((m, a) => m.Name == "GetVideoByID" && (int)a![0]! == 9 ? video : null);
using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => {
    services.AddRouting(); services.AddLogging(); services.AddSingleton(users); services.AddSingleton(videos);
    services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
    services.AddAuthorization(); Plugin.RegisterServices(services, null!);
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
Console.WriteLine($"{passed} checks passed. ABI: {typeof(Shoko.Abstractions.Plugin.IPlugin).Assembly.GetName().Version}");

public class FakeClock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
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
