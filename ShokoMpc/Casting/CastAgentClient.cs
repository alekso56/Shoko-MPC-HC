using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShokoMpc.Casting;

public sealed class CastAgentClient(HttpClient client, ICastSettingsSource settings, TimeSpan? timeout = null) : ICastAgent
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan TargetsTimeout = TimeSpan.FromSeconds(10);
    internal const int MaxRequestBytes = 4096;
    internal const string UnavailableMessage = "The TV caster is not responding.";
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<CastState>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public async Task<CastStatus> StartAsync(CastRequest request, CancellationToken cancellation)
        => await SendAsync<CastStatus>(HttpMethod.Post, request, cancellation) ?? throw new CastAgentUnavailableException(UnavailableMessage);

    public async Task<CastStatus> GetStatusAsync(CancellationToken cancellation)
        => await SendAsync<CastStatus>(HttpMethod.Get, null, cancellation) ?? throw new CastAgentUnavailableException(UnavailableMessage);

    public async Task<IReadOnlyList<CastTarget>> GetTargetsAsync(CancellationToken cancellation)
    {
        var entries = await SendAsync<JsonElement>(HttpMethod.Get, null, cancellation, "/v1/targets", TargetsTimeout);
        if (entries.ValueKind != JsonValueKind.Array)
        {
            throw new CastAgentUnavailableException(UnavailableMessage);
        }

        var targets = new List<CastTarget>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                ReadString(entry, "id") is not { } id || !CastTarget.IsValidId(id) ||
                ReadString(entry, "kind") is not { } kind || kind is not ("miracast" or "dlna" or "cast") ||
                ReadString(entry, "name") is not { } rawName ||
                !entry.TryGetProperty("available", out var available) ||
                available.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                continue;
            }

            var name = new string(rawName.Where(character => !char.IsControl(character)).ToArray());
            if (name.EnumerateRunes().Count() is >= 1 and <= 64)
            {
                targets.Add(new CastTarget(id, name, kind, available.GetBoolean()));
            }
        }

        return targets;
    }

    public async Task StopAsync(CancellationToken cancellation)
        => await SendAsync<CastStatus>(HttpMethod.Delete, null, cancellation);

    private async Task<T?> SendAsync<T>(HttpMethod method, CastRequest? request, CancellationToken cancellation,
        string path = "/v1/cast", TimeSpan? requestTimeout = null)
    {
        var config = settings.Read() ?? throw new CastAgentUnavailableException(UnavailableMessage);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(requestTimeout ?? timeout ?? DefaultTimeout);
        using var message = new HttpRequestMessage(method, new Uri(config.AgentUrl, path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AgentSecret);
        if (request is not null)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(request, json);
            if (body.Length > MaxRequestBytes)
            {
                throw new CastAgentUnavailableException("The TV caster request was too large.");
            }

            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (method == HttpMethod.Post && response.StatusCode == HttpStatusCode.BadRequest)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(json, deadline.Token);
                var rejection = body.ValueKind == JsonValueKind.Object ? ReadString(body, "error") : null;
                if (rejection is "Unknown cast target." or "This TV type isn't supported yet.")
                {
                    throw new CastTargetRejectedException(rejection);
                }
            }

            response.EnsureSuccessStatusCode();
            return method == HttpMethod.Delete
                ? default
                : await response.Content.ReadFromJsonAsync<T>(json, deadline.Token);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException ||
            error is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            // Transport and parsing exceptions can contain request or response data.
            throw new CastAgentUnavailableException(UnavailableMessage);
        }
    }

    private static string? ReadString(JsonElement entry, string property)
    {
        if (!entry.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try { return value.GetString(); }
        catch (InvalidOperationException) { return null; }
    }
}
