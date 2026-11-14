using System.Text.Json;
using Shoko.Abstractions.Config.Services;

namespace ShokoSeasons;

public sealed record SonarrSettings(string Url, string ApiKey, int QualityProfile, string RootFolder);

public sealed class SonarrConnection(IConfigurationService configurations)
{
    public SonarrSettings Read()
    {
        var info = configurations.GetAllConfigurationInfos().FirstOrDefault(x => x.Type.FullName == "Shokarr.Config.ShokarrConfiguration");
        if (info is null) { throw new SeasonException("Install Shokarr and configure its Sonarr connection first.", 503); }
        // Use the native configuration service. Secret values stay inside the server.
        var config = JsonSerializer.Deserialize<ShokarrSettings>(configurations.Serialize(configurations.Load(info)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var profile = config.SonarrQualityProfile.Options.FirstOrDefault(x => x.IsSelected)?.Value ?? 0;
        var folder = config.SonarrRootFolder.Options.FirstOrDefault(x => x.IsSelected)?.Value ?? "";
        if (!Uri.TryCreate(config.SonarrUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            string.IsNullOrWhiteSpace(config.SonarrApiKey) || profile <= 0 || string.IsNullOrWhiteSpace(folder))
        {
            throw new SeasonException("Choose a Sonarr URL, API key, quality profile and root folder in Shokarr settings.", 503);
        }
        return new(config.SonarrUrl.TrimEnd('/'), config.SonarrApiKey, profile, folder);
    }

    private sealed class Option<T> { public T? Value { get; set; } public bool IsSelected { get; set; } }
    private sealed class Selection<T> { public Option<T>[] Options { get; set; } = []; }
    private sealed class ShokarrSettings
    {
        public string SonarrUrl { get; set; } = "";
        public string SonarrApiKey { get; set; } = "";
        public Selection<int> SonarrQualityProfile { get; set; } = new();
        public Selection<string> SonarrRootFolder { get; set; } = new();
    }
}
