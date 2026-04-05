using System.Text;
using System.Text.Json;

namespace PhlanMic.Host.Core;

public static class TransportControlMessageProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Serialize(TransportControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        message.Validate();
        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static TransportControlMessage Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new TransportProtocolException("Control message payload cannot be empty.");
        }

        var message = JsonSerializer.Deserialize<TransportControlMessage>(json, JsonOptions)
            ?? throw new TransportProtocolException("Control message payload was not valid JSON.");

        message.Validate();
        return message;
    }

    public static async Task WriteAsync(
        Stream stream,
        TransportControlMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var json = Serialize(message);
        var bytes = Encoding.UTF8.GetBytes($"{json}\n");
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<TransportControlMessage?> ReadAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var line = await reader.ReadLineAsync(cancellationToken);
        return line is null ? null : Deserialize(line);
    }
}
