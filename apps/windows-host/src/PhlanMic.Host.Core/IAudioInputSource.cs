namespace PhlanMic.Host.Core;

public interface IAudioInputSource
{
    event EventHandler<StreamSessionSnapshot>? SessionChanged;

    StreamSessionSnapshot GetSessionSnapshot();

    StreamStatisticsSnapshot GetStatisticsSnapshot();

    Task RunAsync(CancellationToken cancellationToken);
}
