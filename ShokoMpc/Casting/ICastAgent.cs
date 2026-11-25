using System.Text.Json.Serialization;

namespace ShokoMpc.Casting;

public sealed record CastRequest(string MediaUrl, string Title, double StartSeconds, string ProgressUrl, string CompleteUrl);

public enum CastState { Idle, Connecting, Playing, Stopping, Error }

public sealed record CastStatus([property: JsonRequired] CastState State, string? Title, double? PositionSeconds, string? Error);

public interface ICastAgent
{
    Task<CastStatus> StartAsync(CastRequest request, CancellationToken cancellation);
    Task<CastStatus> GetStatusAsync(CancellationToken cancellation);
    Task StopAsync(CancellationToken cancellation);
}

public sealed class CastAgentUnavailableException(string message) : Exception(message);
