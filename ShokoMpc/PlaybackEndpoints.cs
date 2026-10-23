using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Services;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Services;

namespace ShokoMpc;

public static class PlaybackEndpoints
{
    public static string DisplayFileName(IVideo video)
    {
        // Metadata is a display label only. It never chooses a file on disk.
        var name = (video.EarliestKnownName ?? "").Replace('\\', '/').Split('/').Last();
        name = new string(name.Select(ch => char.IsControl(ch) || "<>:\"/\\|?*%".Contains(ch) ? '_' : ch).ToArray()).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(name) ? $"Shoko video {video.LocalID}.mkv" : name;
    }
    public sealed record LaunchRequest(int[] FileIds);
    public static bool CanPlay(IUser? user, IVideo? video) => user is not null && video is not null
        && video.Series.Count > 0 && video.Series.All(user.IsAllowedToSee);

    private static void PrivateResponse(HttpContext c)
    {
        c.Response.Headers.CacheControl = "no-store";
        c.Response.Headers["Referrer-Policy"] = "no-referrer";
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
    private static Task Error(HttpContext c, int status, string error)
    {
        c.Response.StatusCode = status;
        return c.Response.WriteAsJsonAsync(new { error });
    }
    public static async Task Launch(HttpContext c)
    {
        PrivateResponse(c);
        // Require an explicit authenticated API header, not a cookie or query token, for this mutation.
        if (string.IsNullOrWhiteSpace(c.Request.Headers["apikey"]) &&
            !c.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        { await Error(c, 401, "Sign in to Shoko before starting playback."); return; }
        if (c.Request.ContentLength > 4096) { await Error(c, 413, "Request too large."); return; }
        LaunchRequest? request;
        try { request = await c.Request.ReadFromJsonAsync<LaunchRequest>(c.RequestAborted); }
        catch (Exception e) when (e is System.Text.Json.JsonException or BadHttpRequestException)
        { await Error(c, 400, "Invalid playback request."); return; }
        if (request?.FileIds is not { Length: > 0 and <= 16 } || request.FileIds.Any(x => x <= 0))
        { await Error(c, 400, "Choose between one and sixteen files."); return; }
        var user = c.RequestServices.GetRequiredService<IUserService>().GetUserFromHttpContext(c);
        var videos = c.RequestServices.GetRequiredService<IVideoService>();
        foreach (var id in request.FileIds)
        {
            if (!CanPlay(user, videos.GetVideoByID(id)))
            { await Error(c, 404, "This file is unavailable to your Shoko account."); return; }
        }

        try
        {
            var token = c.RequestServices.GetRequiredService<PlaybackTokens>().IssueLaunch(user!.LocalID, request.FileIds);
            await c.Response.WriteAsJsonAsync(new { ticket = token, expiresInSeconds = 90 });
        }
        catch (InvalidOperationException e) { await Error(c, 429, e.Message); }
    }
    public static async Task Resolve(HttpContext c)
    {
        PrivateResponse(c);
        var token = c.Request.RouteValues["token"]?.ToString() ?? "";
        var tokens = c.RequestServices.GetRequiredService<PlaybackTokens>();
        var grant = tokens.Redeem(token);
        if (grant is null) { await Error(c, 410, "This play link expired or was already used. Click Play again."); return; }
        var user = c.RequestServices.GetRequiredService<IUserService>().GetUserByID(grant.UserId);
        var videos = c.RequestServices.GetRequiredService<IVideoService>();
        if (grant.FileIds.Any(id => !CanPlay(user, videos.GetVideoByID(id))))
        { await Error(c, 404, "This file is no longer available to your account."); return; }
        try
        {
            var paths = grant.FileIds.Select(id => Plugin.BasePath + "/stream/" + tokens.IssueStream(grant.UserId, id)).ToArray();
            // Keep token-only paths for older Windows handlers; updated handlers prefer the labelled paths.
            var namedPaths = paths.Select((path, index) => path + "/" + Uri.EscapeDataString(DisplayFileName(videos.GetVideoByID(grant.FileIds[index])!))).ToArray();
            await c.Response.WriteAsJsonAsync(new { paths, namedPaths, expiresInHours = 12 });
        }
        catch (InvalidOperationException e) { await Error(c, 429, e.Message); }
    }
    public static async Task Stream(HttpContext c)
    {
        PrivateResponse(c);
        var token = c.Request.RouteValues["token"]?.ToString() ?? "";
        var grant = c.RequestServices.GetRequiredService<PlaybackTokens>().FindStream(token);
        if (grant is null) { await Error(c, 410, "Playback session expired. Click Play again in Shoko."); return; }
        var user = c.RequestServices.GetRequiredService<IUserService>().GetUserByID(grant.UserId);
        var video = c.RequestServices.GetRequiredService<IVideoService>().GetVideoByID(grant.FileIds[0]);
        if (!CanPlay(user, video)) { await Error(c, 404, "Video unavailable."); return; }
        var stream = video!.GetStream();
        if (stream is null) { await Error(c, 404, "Video file is offline."); return; }
        // ASP.NET owns and disposes this stream; byte ranges allow MPC-HC seeking without downloading the file.
        await Results.Stream(stream, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(c);
    }
}
