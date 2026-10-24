using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Enums;
using Shoko.Abstractions.Plugin.Models;

namespace ShokoMpc;

public sealed class Plugin : IPlugin, IPluginServiceRegistration, IPluginApplicationRegistration
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
    public static void RegisterServices(IServiceCollection services, IApplicationPaths paths)
    {
        services.AddSingleton<PlaybackTokens>();
        services.AddSingleton<CompletionSessions>();
        services.AddSingleton<IStartupFilter, WebUiStartupFilter>();
    }
    public static void RegisterServices(IApplicationBuilder app, IApplicationPaths paths)
        => app.UseEndpoints(routes =>
        {
            routes.MapPost(BasePath + "/launch", PlaybackEndpoints.Launch).RequireAuthorization();
            routes.MapPost(BasePath + "/resolve/{token}", PlaybackEndpoints.Resolve);
            routes.MapPost(BasePath + "/complete/{token}/{part:int}", PlaybackEndpoints.Complete);
            routes.MapGet(BasePath + "/stream/{token}", PlaybackEndpoints.Stream);
            routes.MapMethods(BasePath + "/stream/{token}", ["HEAD"], PlaybackEndpoints.Stream);
            routes.MapMethods(BasePath + "/stream/{token}/{filename}", ["GET", "HEAD"], PlaybackEndpoints.Stream);
        });
}
