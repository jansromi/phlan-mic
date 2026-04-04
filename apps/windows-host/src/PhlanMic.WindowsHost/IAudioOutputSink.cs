namespace PhlanMic.WindowsHost;

internal interface IAudioOutputSink : IDisposable
{
    AudioOutputSnapshot GetSnapshot();

    Task RunAsync(CancellationToken cancellationToken);
}
