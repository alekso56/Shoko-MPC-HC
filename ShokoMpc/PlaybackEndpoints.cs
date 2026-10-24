using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Services;
using Shoko.Abstractions.User.Enums;
using Shoko.Abstractions.User.Update;
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
            var sessions = c.RequestServices.GetRequiredService<CompletionSessions>();
            await sessions.ProgressGate.WaitAsync(c.RequestAborted);
            try
            {
                var sessionToken = sessions.Issue(grant.UserId, grant.FileIds);
                var completionPath = Plugin.BasePath + "/complete/" + sessionToken;
                var progressPath = Plugin.BasePath + "/progress/" + sessionToken;
                var data = c.RequestServices.GetRequiredService<IUserDataService>();
                var resumeSeconds = grant.FileIds.Select(id => data.GetVideoUserData(videos.GetVideoByID(id)!, user!)?.ProgressPosition.TotalSeconds ?? 0).ToArray();
                await c.Response.WriteAsJsonAsync(new { paths, namedPaths, completionPath, progressPath, resumeSeconds, watchedThreshold = 0.9, expiresInHours = 12 });
            }
            finally { sessions.ProgressGate.Release(); }
        }
        catch (InvalidOperationException e) { await Error(c, 429, e.Message); }
    }
    public sealed record ProgressRequest(long Sequence, double Seconds, double Duration);
    public static Task Complete(HttpContext c) => SaveProgress(c, false);
    public static Task Progress(HttpContext c) => SaveProgress(c, true);
    private static async Task SaveProgress(HttpContext c, bool reportPosition)
    {
        PrivateResponse(c);
        var token = c.Request.RouteValues["token"]?.ToString() ?? "";
        var sessions = c.RequestServices.GetRequiredService<CompletionSessions>();
        var session = sessions.Find(token);
        if (session is null) { await Error(c, 410, "Watched-sync session expired. Reopen the episode from Shoko."); return; }
        if (!int.TryParse(c.Request.RouteValues["part"]?.ToString(), out var part) || part < 0 || part >= session.Finished.Length)
        { await Error(c, 400, "Invalid episode part."); return; }
        ProgressRequest? request = null;
        if (reportPosition)
        {
            if (c.Request.ContentLength > 1024) { await Error(c, 413, "Request too large."); return; }
            try { request = await c.Request.ReadFromJsonAsync<ProgressRequest>(c.RequestAborted); }
            catch (Exception e) when (e is System.Text.Json.JsonException or BadHttpRequestException)
            { await Error(c, 400, "Invalid progress report."); return; }
            if (request is null || request.Sequence <= 0 || !double.IsFinite(request.Seconds) || !double.IsFinite(request.Duration)
                || request.Duration <= 0 || request.Duration > TimeSpan.FromDays(7).TotalSeconds || request.Seconds < 0 || request.Seconds > request.Duration + 2)
            { await Error(c, 400, "Invalid playback position or duration."); return; }
        }
        await sessions.ProgressGate.WaitAsync(c.RequestAborted);
        try
        {
            if (!sessions.IsCurrent(session)) { await Error(c, 409, "A newer playback session took over this episode."); return; }
            var user = c.RequestServices.GetRequiredService<IUserService>().GetUserByID(session.Playback.UserId);
            var service = c.RequestServices.GetRequiredService<IVideoService>();
            var videos = session.Playback.FileIds.Select(service.GetVideoByID).ToArray();
            if (videos.Any(video => !CanPlay(user, video)))
            { await Error(c, 404, "This episode is no longer available to your account."); return; }
            var data = c.RequestServices.GetRequiredService<IUserDataService>();
            if (request is not null)
            {
                if (request.Sequence <= session.Sequences[part]) { await c.Response.WriteAsJsonAsync(new { saved = true }); return; }
                if (!session.Finished[part])
                {
                    var finished = request.Seconds >= request.Duration * 0.9;
                    await data.SaveVideoUserData(videos[part]!, user!, new VideoUserDataUpdate
                    {
                        ProgressPosition = TimeSpan.FromSeconds(finished ? 0 : request.Seconds),
                        ApplyCompletionThreshold = false
                    }, VideoUserDataSaveReason.PlaybackProgress, updateStatsNow: false);
                    session.Finished[part] = finished;
                }
            }
            else session.Finished[part] = true;
            var complete = session.Finished.All(x => x);
            if (complete)
            {
                foreach (var video in videos)
                {
                    if (session.Saved.Contains(video!.LocalID)) continue;
                    await data.SetVideoWatchedStatus(video, user!, true, reason: VideoUserDataSaveReason.PlaybackEnd);
                    session.Saved.Add(video.LocalID);
                }
            }
            if (request is not null) session.Sequences[part] = request.Sequence;
            await c.Response.WriteAsJsonAsync(new { watched = complete });
        }
        finally { sessions.ProgressGate.Release(); }
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
