using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost;

public sealed class HostConfigLoader
{
    private readonly HostConfigStore configStore;

    public HostConfigLoader()
        : this(new HostConfigStore())
    {
    }

    public HostConfigLoader(HostConfigStore configStore)
    {
        this.configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
    }

    public HostRuntimeConfig Load(string configPath)
        => configStore.LoadEffective(configPath);
}
