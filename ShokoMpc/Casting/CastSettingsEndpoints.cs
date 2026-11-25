using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Shoko.Abstractions.User.Services;

namespace ShokoMpc.Casting;

internal static class CastSettingsEndpoints
{
    private static readonly object configurationLock = new();
    private const string InvalidSettings = "Invalid settings.";
    private const string AdminOnly = "Only Shoko administrators can change TV casting settings.";

    private sealed record SettingsRequest(string? AgentUrl, string? LanBaseUrl, string? AgentSecret);

    public static IResult Get(HttpContext context, IUserService users, ICastConfigurationStore store)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (users.GetUserFromHttpContext(context)?.IsAdmin != true)
        {
            return Results.Json(new { error = AdminOnly }, statusCode: StatusCodes.Status403Forbidden);
        }

        lock (configurationLock)
        {
            return SettingsResponse(store.Load());
        }
    }

    public static async Task<IResult> Put(HttpContext context, IUserService users, ICastConfigurationStore store)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (users.GetUserFromHttpContext(context)?.IsAdmin != true)
        {
            return Results.Json(new { error = AdminOnly }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!context.Request.HasJsonContentType())
        {
            return BadRequest(InvalidSettings);
        }

        SettingsRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<SettingsRequest>(context.RequestAborted);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return BadRequest(InvalidSettings);
        }

        if (request is null)
        {
            return BadRequest(InvalidSettings);
        }

        var agentUrl = request.AgentUrl?.Trim() ?? "";
        if (!IsSettingsUrl(agentUrl))
        {
            return BadRequest("Enter a valid agent URL starting with http:// or https://.");
        }

        var lanBaseUrl = request.LanBaseUrl?.Trim() ?? "";
        if (!IsSettingsUrl(lanBaseUrl))
        {
            return BadRequest("Enter a valid LAN base URL starting with http:// or https://.");
        }

        var secret = request.AgentSecret?.Trim() ?? "";
        if (secret.Length > 0 && (secret.Length < 32 || !secret.All(ch => char.IsAsciiLetterOrDigit(ch) || "-._~+/=".Contains(ch))))
        {
            return BadRequest("The agent secret must be at least 32 characters: letters, digits or - . _ ~ + / =.");
        }

        // Keep the saved secret current when a URL-only update overlaps a secret replacement.
        lock (configurationLock)
        {
            if (secret.Length == 0)
            {
                var savedSecret = store.Load().AgentSecret;
                if (string.IsNullOrWhiteSpace(savedSecret))
                {
                    return BadRequest("Enter the agent secret.");
                }

                secret = savedSecret;
            }

            var configuration = new Plugin.Configuration { AgentUrl = agentUrl, LanBaseUrl = lanBaseUrl, AgentSecret = secret };
            store.Save(configuration);
            return SettingsResponse(configuration);
        }
    }

    private static bool IsSettingsUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is ("http" or "https") &&
            url.IsWellFormedOriginalString() && !url.GetLeftPart(UriPartial.Authority).Contains('@') &&
            url.Query.Length == 0 && url.Fragment.Length == 0;

    private static IResult SettingsResponse(Plugin.Configuration configuration) => Results.Json(new
    {
        agentUrl = configuration.AgentUrl ?? "",
        lanBaseUrl = configuration.LanBaseUrl ?? "",
        secretSet = !string.IsNullOrWhiteSpace(configuration.AgentSecret)
    });

    private static IResult BadRequest(string error) => Results.BadRequest(new { error });
}
