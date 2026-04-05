using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost;

internal sealed class WindowsHostApp
{
    private readonly WindowsHostRuntime runtime;

    public WindowsHostApp(StructuredConsoleLogger logger, HostRuntimeConfig config)
    {
        runtime = new WindowsHostRuntime(logger, config);
    }

    public Task RunAsync(CancellationToken cancellationToken) =>
        runtime.RunUntilStoppedAsync(cancellationToken);
}
