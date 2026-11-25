using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Services;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Services;

namespace ShokoMpc.Casting;

internal static class CastEndpoints
{
    private const string NotConfigured = "Set up TV casting in the plugin settings.";
    private const string InvalidRequest = "Invalid cast request.";
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private static readonly Regex capability = new("[0-9a-fA-F]{64}");

    public static Task Start(HttpContext context) => Run(context, StartCast);

    public static Task GetStatus(HttpContext context) => Run(context, async context =>
    {
        if (await ReadSettings(context) is not { } settings)
        {
            return;
        }

        var status = await Agent(context).GetStatusAsync(context.RequestAborted);
        await context.Response.WriteAsJsonAsync(new
        {
            state = status.State.ToString().ToLowerInvariant(),
            title = SafeStatusText(status.Title, settings.AgentSecret),
            positionSeconds = status.PositionSeconds,
            error = SafeStatusText(status.Error, settings.AgentSecret)
        }, json, context.RequestAborted);
    });

    public static Task Stop(HttpContext context) => Run(context, async context =>
    {
        if (await ReadSettings(context) is null)
        {
            return;
        }

        await Agent(context).StopAsync(context.RequestAborted);
        await WriteState(context, "stopping");
    });

    private static async Task Run(HttpContext context, Func<HttpContext, Task> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (string.IsNullOrWhiteSpace(context.Request.Headers["apikey"]) &&
            !context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await Error(context, 401, "Sign in to Shoko before starting playback.");
            return;
        }

        try
        {
            await action(context);
        }
        catch (CastAgentUnavailableException)
        {
            await Error(context, 502, CastAgentClient.UnavailableMessage);
        }
    }

    private static async Task StartCast(HttpContext context)
    {
        var request = await ReadRequest(context);
        if (request is null)
        {
            return;
        }

        var services = context.RequestServices;
        var user = services.GetRequiredService<IUserService>().GetUserFromHttpContext(context);
        var videoService = services.GetRequiredService<IVideoService>();
        var videos = request.FileIds.Select(videoService.GetVideoByID).ToArray();
        if (videos.Any(video => !PlaybackEndpoints.CanPlay(user, video)))
        {
            await Error(context, 404, "This file is unavailable to your Shoko account.");
            return;
        }

        if (videos.Length > 1)
        {
            await Error(context, 400, "Multi-part episodes can't play on the TV yet.");
            return;
        }

        if (await ReadSettings(context) is not { } settings)
        {
            return;
        }

        CastRequest cast;
        try
        {
            cast = await CreateCastRequest(context, settings.LanBaseUrl, user!, videos[0]!, request.FileIds);
        }
        catch (InvalidOperationException)
        {
            await Error(context, 429, "Playback session limit reached.");
            return;
        }

        await Agent(context).StartAsync(cast, context.RequestAborted);
        await WriteState(context, "connecting");
    }

    private static async Task<CastSettings?> ReadSettings(HttpContext context)
    {
        var settings = context.RequestServices.GetRequiredService<ICastSettingsSource>().Read();
        if (settings is null)
        {
            await Error(context, 503, NotConfigured);
        }

        return settings;
    }

    private static async Task<PlaybackEndpoints.LaunchRequest?> ReadRequest(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength > CastAgentClient.MaxRequestBytes)
        {
            await Error(context, 413, "Request too large.");
            return null;
        }

        if (!request.HasJsonContentType())
        {
            await Error(context, 400, InvalidRequest);
            return null;
        }

        PlaybackEndpoints.LaunchRequest? payload;
        try
        {
            // Read one extra byte so requests without a Content-Length are limited too.
            var body = new byte[CastAgentClient.MaxRequestBytes + 1];
            var length = await request.Body.ReadAtLeastAsync(body, body.Length, throwOnEndOfStream: false, context.RequestAborted);
            if (length > CastAgentClient.MaxRequestBytes)
            {
                await Error(context, 413, "Request too large.");
                return null;
            }

            payload = JsonSerializer.Deserialize<PlaybackEndpoints.LaunchRequest>(body.AsSpan(0, length), json);
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        {
            await Error(context, 413, "Request too large.");
            return null;
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException)
        {
            await Error(context, 400, InvalidRequest);
            return null;
        }

        if (payload?.FileIds is not { Length: > 0 and <= 16 } || payload.FileIds.Any(id => id <= 0))
        {
            await Error(context, 400, "Choose between one and sixteen files.");
            return null;
        }

        return payload;
    }

    private static async Task<CastRequest> CreateCastRequest(HttpContext context, Uri lanBaseUrl, IUser user, IVideo video, int[] fileIds)
    {
        var services = context.RequestServices;
        var lanPrefix = lanBaseUrl.AbsoluteUri.TrimEnd('/') + Plugin.BasePath;
        var title = PlaybackEndpoints.DisplayFileName(video);
        var streamToken = services.GetRequiredService<PlaybackTokens>().IssueStream(user.LocalID, fileIds[0]);
        var sessions = services.GetRequiredService<CompletionSessions>();
        await sessions.ProgressGate.WaitAsync(context.RequestAborted);
        try
        {
            var sessionToken = sessions.Issue(user.LocalID, fileIds);
            var data = services.GetRequiredService<IUserDataService>();
            var startSeconds = data.GetVideoUserData(video, user)?.ProgressPosition.TotalSeconds ?? 0;
            return new($"{lanPrefix}/stream/{streamToken}/{Uri.EscapeDataString(title)}", title, startSeconds,
                $"{lanPrefix}/progress/{sessionToken}/0", $"{lanPrefix}/complete/{sessionToken}/0");
        }
        finally
        {
            sessions.ProgressGate.Release();
        }
    }

    private static ICastAgent Agent(HttpContext context) => context.RequestServices.GetRequiredService<ICastAgent>();

    private static Task WriteState(HttpContext context, string state)
    {
        context.Response.StatusCode = 202;
        return context.Response.WriteAsJsonAsync(new { state }, json, context.RequestAborted);
    }

    private static Task Error(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error }, json, context.RequestAborted);
    }

    private static string? SafeStatusText(string? text, string secret)
        => text is null ? null : capability.Replace(text.Replace(secret, "[redacted]", StringComparison.Ordinal), "[redacted]");
}
