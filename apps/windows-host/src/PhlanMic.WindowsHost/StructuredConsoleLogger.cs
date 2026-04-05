using System.Collections;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PhlanMic.WindowsHost;

public enum StructuredLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error
}

public enum StructuredConsoleLogFormat
{
    Text,
    Json
}

public static class StructuredLogLevelParser
{
    public static StructuredLogLevel Parse(string value) =>
        Enum.TryParse<StructuredLogLevel>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Unsupported log level '{value}'.");
}

public static class StructuredConsoleLogFormatParser
{
    public static StructuredConsoleLogFormat Parse(string value) =>
        Enum.TryParse<StructuredConsoleLogFormat>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Unsupported log format '{value}'.");
}

public sealed class StructuredConsoleLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public StructuredLogLevel MinimumLevel { get; set; } = StructuredLogLevel.Information;

    public StructuredConsoleLogFormat OutputFormat { get; set; } = StructuredConsoleLogFormat.Text;

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

        if (OutputFormat == StructuredConsoleLogFormat.Json)
        {
            WriteJson(level, eventName, message, exception, properties);
            return;
        }

        WriteText(level, eventName, message, exception, properties);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string FormatLevel(StructuredLogLevel level) =>
        level switch
        {
            StructuredLogLevel.Trace => "TRC",
            StructuredLogLevel.Debug => "DBG",
            StructuredLogLevel.Information => "INF",
            StructuredLogLevel.Warning => "WRN",
            StructuredLogLevel.Error => "ERR",
            _ => level.ToString().ToUpperInvariant()
        };

    private static string QuoteIfNeeded(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var requiresQuotes = value.Any(char.IsWhiteSpace) || value.Any(character => character is '=' or ':' or ',' or ';' or '[' or ']' or '{' or '}');
        if (!requiresQuotes)
        {
            return value;
        }

        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string FormatValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        return value switch
        {
            string stringValue => QuoteIfNeeded(stringValue),
            bool boolValue => boolValue ? "true" : "false",
            DateTimeOffset dateTimeOffset => FormatTimestamp(dateTimeOffset),
            DateTime dateTime => FormatTimestamp(new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc))),
            IDictionary dictionary => FormatDictionary(dictionary),
            IEnumerable enumerable when value is not string => FormatEnumerable(enumerable),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string FormatDictionary(IDictionary dictionary)
    {
        var entries = new List<string>();

        foreach (DictionaryEntry entry in dictionary)
        {
            entries.Add($"{entry.Key}={FormatValue(entry.Value)}");
        }

        return $"{{{string.Join(", ", entries)}}}";
    }

    private static string FormatEnumerable(IEnumerable enumerable)
    {
        var items = new List<string>();

        foreach (var item in enumerable)
        {
            items.Add(FormatValue(item));
        }

        return $"[{string.Join(", ", items)}]";
    }

    private static string FormatProperties(
        string eventName,
        IReadOnlyDictionary<string, object?>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return string.Empty;
        }

        if (eventName == "stream_stats")
        {
            return FormatStreamStats(properties);
        }

        if (eventName == "audio_output_summary")
        {
            return FormatAudioOutputSummary(properties);
        }

        return string.Join(" | ", properties.Select(property => $"{property.Key}={FormatValue(property.Value)}"));
    }

    private static string FormatStreamStats(IReadOnlyDictionary<string, object?> properties)
    {
        var input = string.Join(
            " ",
            new[]
            {
                $"bytes={FormatProperty(properties, "bytesReceived")}",
                $"packets={FormatProperty(properties, "packetsReceived")}",
                $"frames={FormatProperty(properties, "framesReceived")}",
                $"accepted={FormatProperty(properties, "acceptedFrames")}",
                $"rejected={FormatProperty(properties, "rejectedFrames")}",
                $"dropped={FormatProperty(properties, "droppedFrames")}",
                $"buffered={FormatProperty(properties, "bufferedFrames")}"
            });

        var robustness = string.Join(
            " ",
            new[]
            {
                $"state={FormatProperty(properties, "streamRobustnessState")}",
                $"nextSeq={FormatProperty(properties, "expectedNextSequence")}",
                $"gaps={FormatProperty(properties, "sequenceGapsObserved")}",
                $"late={FormatProperty(properties, "lateFramesArrived")}/{FormatProperty(properties, "lateFramesDropped")}",
                $"missing={FormatProperty(properties, "missingFramesDetected")}",
                $"silence={FormatProperty(properties, "hostSilenceFramesInserted")}",
                $"prebuffer={FormatProperty(properties, "currentPrebufferDepth")}"
            });

        var output = string.Join(
            " ",
            new[]
            {
                $"sink={FormatProperty(properties, "outputSink")}",
                $"device={FormatProperty(properties, "outputDeviceName")}",
                $"buffered={FormatProperty(properties, "outputBufferedFrames")}",
                $"submitted={FormatProperty(properties, "outputSubmittedFrames")}",
                $"completed={FormatProperty(properties, "outputCompletedFrames")}",
                $"underruns={FormatProperty(properties, "underrunCount")}",
                $"latencyMs={FormatProperty(properties, "estimatedLatencyMs")}",
                $"glitchRate={FormatProperty(properties, "glitchRatePerMinute")}"
            });

        return $"input[{input}] robustness[{robustness}] output[{output}]";
    }

    private static string FormatAudioOutputSummary(IReadOnlyDictionary<string, object?> properties)
    {
        var session = string.Join(
            " ",
            new[]
            {
                $"scope={FormatProperty(properties, "summaryScope")}",
                $"state={FormatProperty(properties, "sessionState")}",
                $"transport={FormatProperty(properties, "transportMode")}",
                $"connection={FormatProperty(properties, "connectionId")}",
                $"connections={FormatProperty(properties, "connectionCount")}",
                $"disconnects={FormatProperty(properties, "disconnectCount")}"
            });

        var input = string.Join(
            " ",
            new[]
            {
                $"frames={FormatProperty(properties, "inputFramesReceived")}",
                $"accepted={FormatProperty(properties, "acceptedFrames")}",
                $"rejected={FormatProperty(properties, "rejectedFrames")}",
                $"dropped={FormatProperty(properties, "droppedFrames")}",
                $"buffered={FormatProperty(properties, "bufferedFrames")}"
            });

        var output = string.Join(
            " ",
            new[]
            {
                $"sink={FormatProperty(properties, "outputSink")}",
                $"device={FormatProperty(properties, "outputDeviceName")}",
                $"buffered={FormatProperty(properties, "outputBufferedFrames")}",
                $"submitted={FormatProperty(properties, "outputSubmittedFrames")}",
                $"completed={FormatProperty(properties, "outputCompletedFrames")}",
                $"underruns={FormatProperty(properties, "underrunCount")}",
                $"latencyMs={FormatProperty(properties, "estimatedLatencyMs")}"
            });

        return $"{session} input[{input}] output[{output}]";
    }

    private static string FormatProperty(IReadOnlyDictionary<string, object?> properties, string name) =>
        properties.TryGetValue(name, out var value)
            ? FormatValue(value)
            : "null";

    private void WriteText(
        StructuredLogLevel level,
        string eventName,
        string message,
        Exception? exception,
        IReadOnlyDictionary<string, object?>? properties)
    {
        var builder = new StringBuilder()
            .Append('[')
            .Append(FormatTimestamp(DateTimeOffset.UtcNow))
            .Append("] ")
            .Append(FormatLevel(level))
            .Append(' ')
            .Append(eventName)
            .Append(": ")
            .Append(message);

        var formattedProperties = FormatProperties(eventName, properties);
        if (!string.IsNullOrWhiteSpace(formattedProperties))
        {
            builder.Append(" | ").Append(formattedProperties);
        }

        if (exception is not null)
        {
            builder.Append(" | exception=")
                .Append(exception.GetType().Name)
                .Append(": ")
                .Append(exception.Message);
        }

        Console.WriteLine(builder.ToString());
    }

    private void WriteJson(
        StructuredLogLevel level,
        string eventName,
        string message,
        Exception? exception,
        IReadOnlyDictionary<string, object?>? properties)
    {
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
