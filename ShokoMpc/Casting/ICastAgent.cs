using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ShokoMpc.Casting;

public sealed record CastTarget(string Id, string Name, string Kind, bool Available)
{
    private static readonly Regex idPattern = new(@"\A(miracast|(dlna|cast):uuid:[0-9A-Za-z-]{8,64})\z");

    internal static bool IsValidId(string? id) => id is not null && idPattern.IsMatch(id);
}

public sealed record CastRequest(string MediaUrl, string Title, double StartSeconds, string ProgressUrl, string CompleteUrl,
    [property: JsonPropertyName("target"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Target = null);

public enum CastState { Idle, Connecting, Playing, Stopping, Error }

public sealed record CastStatus([property: JsonRequired] CastState State, string? Title, double? PositionSeconds, string? Error,
    [property: JsonPropertyName("target"), JsonConverter(typeof(CastStatus.TargetConverter))] string? Target = null)
{
    internal sealed class TargetConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return reader.GetString();

            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }
}

public interface ICastAgent
{
    Task<CastStatus> StartAsync(CastRequest request, CancellationToken cancellation);
    Task<CastStatus> GetStatusAsync(CancellationToken cancellation);
    Task<IReadOnlyList<CastTarget>> GetTargetsAsync(CancellationToken cancellation);
    Task StopAsync(CancellationToken cancellation);
}

public sealed class CastAgentUnavailableException(string message) : Exception(message);
public sealed class CastTargetRejectedException(string message) : Exception(message);
