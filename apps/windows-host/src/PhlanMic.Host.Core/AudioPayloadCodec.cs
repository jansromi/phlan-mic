namespace PhlanMic.Host.Core;

public enum AudioPayloadCodec
{
    RawPcm16 = 1,
    Opus = 2
}

public static class AudioPayloadCodecExtensions
{
    public static string ToProtocolValue(this AudioPayloadCodec codec) =>
        codec switch
        {
            AudioPayloadCodec.RawPcm16 => "RawPcm16",
            AudioPayloadCodec.Opus => "Opus",
            _ => throw new InvalidOperationException($"Unsupported payload codec '{codec}'.")
        };

    public static bool TryParseProtocolValue(string? value, out AudioPayloadCodec codec)
    {
        if (string.Equals(value, "RawPcm16", StringComparison.OrdinalIgnoreCase))
        {
            codec = AudioPayloadCodec.RawPcm16;
            return true;
        }

        if (string.Equals(value, "Opus", StringComparison.OrdinalIgnoreCase))
        {
            codec = AudioPayloadCodec.Opus;
            return true;
        }

        codec = default;
        return false;
    }
}
