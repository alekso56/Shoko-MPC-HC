using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Enums;
using Shoko.Abstractions.Plugin.Models;
using ShokoMpc.Casting;

namespace ShokoMpc;

public sealed class Plugin : IPlugin<Plugin.Configuration>, IPluginServiceRegistration, IPluginApplicationRegistration
{
    public const string PluginId = "e84bc763-559a-4a54-9332-69741ec61c25";
    public const string BasePath = "/api/plugin/ShokoMpc";
    public Guid ID => new(PluginId);
    public string Name => "Shoko MPC-HC";
    public string Description => "Play episodes from Shoko's Web UI in MPC-HC on your Windows PC.";
    public IReadOnlyList<PluginPage> GetPages() => [new()
    {
        Name = "MPC-HC setup", Url = BasePath + "/dashboard",
        Visibility = PluginPageVisibility.Authenticated
    }];

    [DisplayName("TV casting")]
    public sealed class Configuration : IPluginConfiguration, IVisibleConfiguration
    {
        [DisplayName("Agent URL")]
        public string AgentUrl { get; set; } = "";

        [DisplayName("Agent secret"), PasswordPropertyText(true)]
        public string AgentSecret { get; set; } = "";

        [DisplayName("LAN base URL")]
        public string LanBaseUrl { get; set; } = "";
    }

    public static void RegisterServices(IServiceCollection services, IApplicationPaths paths)
    {
        services.AddSingleton<PlaybackTokens>();
        services.AddSingleton<CompletionSessions>();
        services.AddSingleton<IStartupFilter, WebUiStartupFilter>();
        services.AddScoped<ICastConfigurationStore, CastConfigurationStore>();
        services.AddScoped<ICastSettingsSource, CastSettingsSource>();
        // CastAgentClient applies a deadline per operation, including response body reads.
        services.AddHttpClient<ICastAgent, CastAgentClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
    }
    public static void RegisterServices(IApplicationBuilder app, IApplicationPaths paths)
        => app.UseEndpoints(routes =>
        {
            routes.MapPost(BasePath + "/launch", PlaybackEndpoints.Launch).RequireAuthorization();
            routes.MapPost(BasePath + "/cast", CastEndpoints.Start).RequireAuthorization();
            routes.MapGet(BasePath + "/cast", CastEndpoints.GetStatus).RequireAuthorization();
            routes.MapDelete(BasePath + "/cast", CastEndpoints.Stop).RequireAuthorization();
            routes.MapGet(BasePath + "/cast/targets", CastEndpoints.GetTargets).RequireAuthorization();
            routes.MapGet(BasePath + "/cast/settings", CastSettingsEndpoints.Get).RequireAuthorization();
            routes.MapPut(BasePath + "/cast/settings", CastSettingsEndpoints.Put).RequireAuthorization();
            routes.MapPost(BasePath + "/resolve/{token}", PlaybackEndpoints.Resolve);
            routes.MapPost(BasePath + "/complete/{token}/{part:int}", PlaybackEndpoints.Complete);
            routes.MapPost(BasePath + "/progress/{token}/{part:int}", PlaybackEndpoints.Progress);
            routes.MapGet(BasePath + "/stream/{token}", PlaybackEndpoints.Stream);
            routes.MapMethods(BasePath + "/stream/{token}", ["HEAD"], PlaybackEndpoints.Stream);
            routes.MapMethods(BasePath + "/stream/{token}/{filename}", ["GET", "HEAD"], PlaybackEndpoints.Stream);
        });
}
