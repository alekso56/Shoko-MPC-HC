using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShokoMpc.Casting;

public sealed class CastAgentClient(HttpClient client, ICastSettingsSource settings, TimeSpan? timeout = null) : ICastAgent
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    internal const int MaxRequestBytes = 4096;
    internal const string UnavailableMessage = "The TV caster is not responding.";
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<CastState>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public async Task<CastStatus> StartAsync(CastRequest request, CancellationToken cancellation)
        => await SendAsync(HttpMethod.Post, request, cancellation) ?? throw new CastAgentUnavailableException(UnavailableMessage);

    public async Task<CastStatus> GetStatusAsync(CancellationToken cancellation)
        => await SendAsync(HttpMethod.Get, null, cancellation) ?? throw new CastAgentUnavailableException(UnavailableMessage);

    public async Task StopAsync(CancellationToken cancellation)
        => await SendAsync(HttpMethod.Delete, null, cancellation);

    private async Task<CastStatus?> SendAsync(HttpMethod method, CastRequest? request, CancellationToken cancellation)
    {
        var config = settings.Read() ?? throw new CastAgentUnavailableException(UnavailableMessage);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        using var message = new HttpRequestMessage(method, new Uri(config.AgentUrl, "/v1/cast"));
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
            response.EnsureSuccessStatusCode();
            return method == HttpMethod.Delete
                ? null
                : await response.Content.ReadFromJsonAsync<CastStatus>(json, deadline.Token);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException ||
            error is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            // Transport and parsing exceptions can contain request or response data.
            throw new CastAgentUnavailableException(UnavailableMessage);
        }
    }
}
