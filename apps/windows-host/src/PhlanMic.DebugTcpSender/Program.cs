using System.Text;
using System.Net.Sockets;
using PhlanMic.DebugTcpSender;
using PhlanMic.Host.Core;

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
    if (string.Equals(options.TransportMode, ReceiverConfig.DebugTcpRawPcmTransportMode, StringComparison.OrdinalIgnoreCase))
    {
        await RunDebugTcpAsync(generator, options, cancellation.Token);
    }
    else
    {
        await RunUdpRawPcmAsync(generator, options, cancellation.Token);
    }

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

static async Task RunDebugTcpAsync(
    PcmSignalGenerator generator,
    DebugTcpSenderOptions options,
    CancellationToken cancellationToken)
{
    using var client = new TcpClient();

    Console.WriteLine($"Connecting to {options.Host}:{options.Port} using {options.TransportMode}...");
    await client.ConnectAsync(options.Host, options.Port, cancellationToken);

    using var stream = client.GetStream();
    await RunPcmLoopAsync(
        options,
        payloadFactory: generator.CreateFramePayload,
        sendPayloadAsync: payload => stream.WriteAsync(payload, cancellationToken).AsTask(),
        cancellationToken);
}

static async Task RunUdpRawPcmAsync(
    PcmSignalGenerator generator,
    DebugTcpSenderOptions options,
    CancellationToken cancellationToken)
{
    long sequenceNumber = 0;
    using var controlClient = new TcpClient();
    Console.WriteLine($"Connecting to {options.Host}:{options.Port} using {options.TransportMode}...");
    await controlClient.ConnectAsync(options.Host, options.Port, cancellationToken);

    using var controlStream = controlClient.GetStream();
    using var controlReader = new StreamReader(controlStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
    using var sendLock = new SemaphoreSlim(1, 1);

    await SendControlMessageAsync(
        controlStream,
        TransportControlMessage.CreateHello(
            "phlan-mic-debug-sender",
            [AudioPayloadCodec.RawPcm16],
            options.KeepAliveIntervalMs,
            options.SessionTimeoutMs),
        sendLock,
        cancellationToken);

    var helloResponse = await ReadRequiredControlMessageAsync(controlReader, cancellationToken);
    if (helloResponse.GetMessageType() is TransportControlMessageType.Error)
    {
        throw new InvalidOperationException(helloResponse.Detail ?? "Host rejected hello.");
    }

    if (helloResponse.GetMessageType() is not TransportControlMessageType.HelloAccepted)
    {
        throw new InvalidOperationException($"Expected helloAccepted but received {helloResponse.Type}.");
    }

    var sessionId = helloResponse.GetRequiredSessionId();
    var audioPort = helloResponse.AudioPort
        ?? throw new InvalidOperationException("Host helloAccepted message did not include an audioPort.");

    await SendControlMessageAsync(
        controlStream,
        TransportControlMessage.CreateStartStream(sessionId, AudioPayloadCodec.RawPcm16, options.Format),
        sendLock,
        cancellationToken);

    var startResponse = await ReadRequiredControlMessageAsync(controlReader, cancellationToken);
    if (startResponse.GetMessageType() is TransportControlMessageType.Error)
    {
        throw new InvalidOperationException(startResponse.Detail ?? "Host rejected startStream.");
    }

    if (startResponse.GetMessageType() is not TransportControlMessageType.StartAccepted)
    {
        throw new InvalidOperationException($"Expected startAccepted but received {startResponse.Type}.");
    }

    using var keepAliveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var keepAliveTask = RunKeepAliveLoopAsync(
        controlStream,
        sessionId,
        options.KeepAliveIntervalMs,
        sendLock,
        keepAliveCancellation.Token);

    try
    {
        using var audioClient = new UdpClient();
        audioClient.Client.Connect(options.Host, audioPort);

        Console.WriteLine($"Control connected. Streaming UDP audio to {options.Host}:{audioPort}.");
        await RunPcmLoopAsync(
            options,
            payloadFactory: generator.CreateFramePayload,
            sendPayloadAsync: async payload =>
            {
                var packet = new TransportAudioPacket(
                    sessionId,
                    AudioPayloadCodec.RawPcm16,
                    Interlocked.Increment(ref sequenceNumber),
                    DateTimeOffset.UtcNow,
                    payload);

                var bytes = TransportAudioPacketSerializer.Serialize(packet);
                await audioClient.Client.SendAsync(bytes, SocketFlags.None, cancellationToken);
            },
            cancellationToken);
    }
    finally
    {
        keepAliveCancellation.Cancel();

        try
        {
            await keepAliveTask;
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await SendControlMessageAsync(
                controlStream,
                TransportControlMessage.CreateStopStream(sessionId, "Sender completed."),
                sendLock,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

static async Task RunPcmLoopAsync(
    DebugTcpSenderOptions options,
    Func<byte[]> payloadFactory,
    Func<byte[], Task> sendPayloadAsync,
    CancellationToken cancellationToken)
{
    Console.WriteLine(
        $"Connected. Sending {(options.FrameCount == 0 ? "until cancelled" : options.FrameCount)} frames " +
        $"as {options.Format.SampleRate}Hz/{options.Format.Channels}ch/{options.Format.BitsPerSample}bit/{options.Format.FrameDurationMs}ms " +
        $"{options.SignalMode} over {options.TransportMode}.");

    var framesSent = 0L;
    var bytesSent = 0L;
    var startedAtUtc = DateTimeOffset.UtcNow;
    var delayPatternIndex = 0;
    var pauseCompleted = false;

    while (!cancellationToken.IsCancellationRequested &&
           (options.FrameCount == 0 || framesSent < options.FrameCount))
    {
        var payload = payloadFactory();
        await sendPayloadAsync(payload);
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
            await Task.Delay(options.PauseDurationMs, cancellationToken);
            pauseCompleted = true;
        }

        var delayMs = options.DelayPatternMs.Count > 0
            ? options.DelayPatternMs[delayPatternIndex++ % options.DelayPatternMs.Count]
            : options.DelayMs;

        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    var elapsed = DateTimeOffset.UtcNow - startedAtUtc;
    Console.WriteLine($"Finished. Sent {framesSent} frames ({bytesSent} bytes) in {elapsed.TotalSeconds:F2}s.");
}

static async Task SendControlMessageAsync(
    Stream controlStream,
    TransportControlMessage message,
    SemaphoreSlim sendLock,
    CancellationToken cancellationToken)
{
    await sendLock.WaitAsync(cancellationToken);
    try
    {
        await TransportControlMessageProtocol.WriteAsync(controlStream, message, cancellationToken);
    }
    finally
    {
        sendLock.Release();
    }
}

static async Task<TransportControlMessage> ReadRequiredControlMessageAsync(
    StreamReader reader,
    CancellationToken cancellationToken)
{
    var line = await reader.ReadLineAsync(cancellationToken);
    if (line is null)
    {
        throw new InvalidOperationException("Host closed the control channel unexpectedly.");
    }

    return TransportControlMessageProtocol.Deserialize(line);
}

static async Task RunKeepAliveLoopAsync(
    Stream controlStream,
    Guid sessionId,
    int keepAliveIntervalMs,
    SemaphoreSlim sendLock,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await Task.Delay(keepAliveIntervalMs, cancellationToken);
        await SendControlMessageAsync(
            controlStream,
            TransportControlMessage.CreateKeepAlive(sessionId),
            sendLock,
            cancellationToken);
    }
}
