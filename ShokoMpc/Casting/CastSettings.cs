using System.Diagnostics.CodeAnalysis;
using Shoko.Abstractions.Config.Services;

namespace ShokoMpc.Casting;

public sealed record CastSettings(Uri AgentUrl, string AgentSecret, Uri LanBaseUrl);

public interface ICastSettingsSource
{
    CastSettings? Read();
}

internal sealed class CastSettingsSource(IConfigurationService configurations) : ICastSettingsSource
{
    // The endpoint and its client use the same settings snapshot for a request.
    private readonly Lazy<CastSettings?> settings = new(() => ReadConfiguration(configurations));

    public CastSettings? Read() => settings.Value;

    private static CastSettings? ReadConfiguration(IConfigurationService configurations)
    {
        var config = configurations.Load<Plugin.Configuration>();
        if (!TryReadUrl(config.AgentUrl, out var agentUrl) || !TryReadUrl(config.LanBaseUrl, out var lanBaseUrl) ||
            string.IsNullOrWhiteSpace(config.AgentSecret) ||
            !config.AgentSecret.All(ch => char.IsAsciiLetterOrDigit(ch) || "-._~+/=".Contains(ch)))
        {
            return null;
        }

        return new(agentUrl, config.AgentSecret, lanBaseUrl);
    }

    private static bool TryReadUrl(string value, [NotNullWhen(true)] out Uri? url)
        => Uri.TryCreate(value, UriKind.Absolute, out url) && url.Scheme is ("http" or "https") &&
            url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0;
}
