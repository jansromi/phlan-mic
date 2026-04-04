using System.Text.Json;

namespace PhlanMic.WindowsHost;

internal enum StructuredLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error
}

internal static class StructuredLogLevelParser
{
    public static StructuredLogLevel Parse(string value) =>
        Enum.TryParse<StructuredLogLevel>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Unsupported log level '{value}'.");
}

internal sealed class StructuredConsoleLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public StructuredLogLevel MinimumLevel { get; set; } = StructuredLogLevel.Information;

    public void Info(string eventName, string message, IReadOnlyDictionary<string, object?>? properties = null) =>
        Write(StructuredLogLevel.Information, eventName, message, null, properties);

    public void Warning(string eventName, string message, IReadOnlyDictionary<string, object?>? properties = null) =>
        Write(StructuredLogLevel.Warning, eventName, message, null, properties);

    public void Error(string eventName, string message, Exception exception, IReadOnlyDictionary<string, object?>? properties = null) =>
        Write(StructuredLogLevel.Error, eventName, message, exception, properties);

    private void Write(
        StructuredLogLevel level,
        string eventName,
        string message,
        Exception? exception,
        IReadOnlyDictionary<string, object?>? properties)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var payload = new Dictionary<string, object?>
        {
            ["timestampUtc"] = DateTimeOffset.UtcNow,
            ["level"] = level.ToString(),
            ["event"] = eventName,
            ["message"] = message
        };

        if (properties is not null)
        {
            foreach (var property in properties)
            {
                payload[property.Key] = property.Value;
            }
        }

        if (exception is not null)
        {
            payload["exceptionType"] = exception.GetType().FullName;
            payload["exceptionMessage"] = exception.Message;
        }

        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
