using Shoko.Abstractions.Config.Services;

namespace ShokoMpc.Casting;

public interface ICastConfigurationStore
{
    Plugin.Configuration Load();
    void Save(Plugin.Configuration configuration);
}

internal sealed class CastConfigurationStore(IConfigurationService configurations) : ICastConfigurationStore
{
    public Plugin.Configuration Load() => configurations.Load<Plugin.Configuration>();

    public void Save(Plugin.Configuration configuration) => configurations.Save(configuration);
}
