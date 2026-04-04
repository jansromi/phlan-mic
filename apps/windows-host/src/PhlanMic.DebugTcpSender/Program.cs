using System.Net.Sockets;
using PhlanMic.DebugTcpSender;

DebugTcpSenderOptions options;

try
{
    options = DebugTcpSenderOptions.Parse(args);
}
catch (DebugTcpSenderUsageException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    var generator = new PcmSignalGenerator(options.Format, options.SignalMode, options.SignalFrequencyHz);
    using var client = new TcpClient();

    Console.WriteLine($"Connecting to {options.Host}:{options.Port}...");
    await client.ConnectAsync(options.Host, options.Port, cancellation.Token);

    using var stream = client.GetStream();
    Console.WriteLine(
        $"Connected. Sending {(options.FrameCount == 0 ? "until cancelled" : options.FrameCount)} frames " +
        $"as {options.Format.SampleRate}Hz/{options.Format.Channels}ch/{options.Format.BitsPerSample}bit/{options.Format.FrameDurationMs}ms " +
        $"{options.SignalMode}.");

    var framesSent = 0L;
    var bytesSent = 0L;
    var startedAtUtc = DateTimeOffset.UtcNow;
    var delayPatternIndex = 0;
    var pauseCompleted = false;

    while (!cancellation.Token.IsCancellationRequested &&
           (options.FrameCount == 0 || framesSent < options.FrameCount))
    {
        var payload = generator.CreateFramePayload();
        await stream.WriteAsync(payload, cancellation.Token);
        framesSent++;
        bytesSent += payload.Length;

        if (framesSent % 50 == 0)
        {
            Console.WriteLine($"Sent {framesSent} frames ({bytesSent} bytes).");
        }

        if (!pauseCompleted &&
            options.PauseAfterFrames > 0 &&
            framesSent == options.PauseAfterFrames &&
            options.PauseDurationMs > 0)
        {
            Console.WriteLine($"Pausing for {options.PauseDurationMs} ms after {framesSent} frames.");
            await Task.Delay(options.PauseDurationMs, cancellation.Token);
            pauseCompleted = true;
        }

        var delayMs = options.DelayPatternMs.Count > 0
            ? options.DelayPatternMs[delayPatternIndex++ % options.DelayPatternMs.Count]
            : options.DelayMs;

        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellation.Token);
        }
    }

    var elapsed = DateTimeOffset.UtcNow - startedAtUtc;
    Console.WriteLine($"Finished. Sent {framesSent} frames ({bytesSent} bytes) in {elapsed.TotalSeconds:F2}s.");
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Sender cancelled.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
