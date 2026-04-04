using System.Net;
using System.Net.Sockets;

namespace PhlanMic.Host.Core;

public sealed class DebugTcpRawPcmReceiver : IAudioInputSource
{
    private readonly ReceiverConfig config;
    private readonly AudioStreamPipeline pipeline;
    private readonly AudioFormat format;
    private readonly AudioInputSessionTracker tracker;

    public DebugTcpRawPcmReceiver(
        ReceiverConfig config,
        AudioFormat format,
        AudioStreamPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(pipeline);

        config.Validate();
        format.Validate();

        this.config = config;
        this.format = format;
        this.pipeline = pipeline;
        tracker = new AudioInputSessionTracker(config.TransportMode);
        tracker.SessionChanged += (_, snapshot) => SessionChanged?.Invoke(this, snapshot);
    }

    public event EventHandler<StreamSessionSnapshot>? SessionChanged;

    public StreamSessionSnapshot GetSessionSnapshot() => tracker.GetSessionSnapshot();

    public StreamStatisticsSnapshot GetStatisticsSnapshot() => tracker.GetStatisticsSnapshot(pipeline);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(ResolveBindAddress(config.BindAddress), config.Port);
        listener.Start();

        var localEndpoint = listener.LocalEndpoint?.ToString();
        tracker.MarkListening(localEndpoint, DateTimeOffset.UtcNow, "Listening for raw PCM TCP connections.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;

                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                    await HandleClientAsync(client, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    tracker.MarkFaulted(DateTimeOffset.UtcNow, exception.Message);
                    tracker.MarkListening(localEndpoint, DateTimeOffset.UtcNow, "Awaiting the next TCP client.");

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
                finally
                {
                    client?.Dispose();
                }
            }
        }
        finally
        {
            listener.Stop();
            tracker.MarkStopped(DateTimeOffset.UtcNow, "TCP receiver stopped.");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString();
        var parser = new RawPcmFrameParser(format);
        tracker.MarkConnected(remoteEndpoint, DateTimeOffset.UtcNow, "TCP client connected.");

        using var stream = client.GetStream();
        var buffer = new byte[Math.Max(format.BytesPerFrame * 4, 4096)];
        var hasEnteredStreamingState = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    tracker.MarkDisconnected(DateTimeOffset.UtcNow, "TCP client disconnected.");
                    break;
                }

                var receivedAtUtc = DateTimeOffset.UtcNow;
                var frames = parser.ParseBytes(buffer.AsSpan(0, bytesRead), receivedAtUtc);
                tracker.RecordTraffic(bytes: bytesRead, packets: 1, frames: frames.Count, atUtc: receivedAtUtc);

                if (!hasEnteredStreamingState && frames.Count > 0)
                {
                    tracker.MarkStreaming(receivedAtUtc, "Receiving PCM audio frames.");
                    hasEnteredStreamingState = true;
                }

                foreach (var frame in frames)
                {
                    pipeline.Write(frame);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException exception) when (!cancellationToken.IsCancellationRequested)
        {
            tracker.MarkDisconnected(DateTimeOffset.UtcNow, exception.Message);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                tracker.MarkListening(tracker.GetSessionSnapshot().LocalEndpoint, DateTimeOffset.UtcNow, "Awaiting the next TCP client.");
            }
        }
    }

    private static IPAddress ResolveBindAddress(string bindAddress)
    {
        if (IPAddress.TryParse(bindAddress, out var parsed))
        {
            return parsed;
        }

        if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        var addresses = Dns.GetHostAddresses(bindAddress);
        var address = addresses.FirstOrDefault(candidate => candidate.AddressFamily is AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"Unable to resolve bind address '{bindAddress}'.");

        return address;
    }
}
