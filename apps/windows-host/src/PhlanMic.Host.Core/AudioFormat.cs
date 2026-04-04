namespace PhlanMic.Host.Core;

public sealed record AudioFormat
{
    public int SampleRate { get; init; } = 48_000;
    public int Channels { get; init; } = 1;
    public int BitsPerSample { get; init; } = 16;
    public int FrameDurationMs { get; init; } = 20;

    public int BytesPerSample => BitsPerSample / 8;

    public int SamplesPerFrame => checked(SampleRate * FrameDurationMs / 1000);

    public int BytesPerFrame => checked(SamplesPerFrame * Channels * BytesPerSample);

    public TimeSpan FrameDuration => TimeSpan.FromMilliseconds(FrameDurationMs);

    public void Validate()
    {
        if (SampleRate <= 0)
        {
            throw new InvalidOperationException("Audio sample rate must be greater than zero.");
        }

        if (Channels <= 0)
        {
            throw new InvalidOperationException("Audio channel count must be greater than zero.");
        }

        if (BitsPerSample is not 16)
        {
            throw new InvalidOperationException("Phase 0 only supports 16-bit signed PCM.");
        }

        if (FrameDurationMs <= 0)
        {
            throw new InvalidOperationException("Frame duration must be greater than zero.");
        }

        if ((SampleRate * FrameDurationMs) % 1000 != 0)
        {
            throw new InvalidOperationException("Frame duration must produce an integer number of samples.");
        }
    }

    public static AudioFormat CreateMvpDefault() =>
        new()
        {
            SampleRate = 48_000,
            Channels = 1,
            BitsPerSample = 16,
            FrameDurationMs = 20
        };
}

