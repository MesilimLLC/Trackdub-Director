namespace Trackdub.Director.Core;

/// <summary>One provider stock voice as advertised by the account voice list.</summary>
/// <param name="Category">Provider category (e.g. premade, cloned). Stock library voices are premade.</param>
/// <param name="Gender">Provider gender label (e.g. female), or null when unlabelled.</param>
/// <param name="ModelIds">Model ids the provider advertises for this voice, when present.</param>
public sealed record TtsVoice(
    string Id,
    string Name,
    string? Category = null,
    string? Gender = null,
    string[]? ModelIds = null);

/// <summary>Raw synthesized audio plus the characters billed for it.</summary>
/// <param name="AudioBytes">PCM or WAV payload in the provider's declared output format.</param>
/// <param name="CharactersBilled">Characters counted against the provider quota (the synthesized text length).</param>
public sealed record TtsSynthesisResult(byte[] AudioBytes, int CharactersBilled);

/// <summary>One synthesis request: the voice, the exact text, the spoken language, and an optional provider speed setting.</summary>
/// <param name="Speed">Provider speed setting (1.0 is normal), or null for the provider default.</param>
public sealed record TtsSynthesisRequest(string VoiceId, string Text, string LanguageCode, double? Speed = null);

/// <summary>
/// Text-to-speech for the audition lane. Implementations must fail closed:
/// <see cref="EnsureConfigured"/> throws before any network call when no API key
/// is configured, so a missing key means zero calls to any lane.
/// </summary>
public interface ITtsProvider
{
    /// <summary>Throws <see cref="TtsApiException"/> when no API key is configured. Makes no network call.</summary>
    void EnsureConfigured();

    /// <summary>List the account's voices (stock library selection happens in the audition service).</summary>
    Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Synthesize one request in one voice. Text-only input: never any reference audio. The returned audio is still measured, never estimated.</summary>
    Task<TtsSynthesisResult> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the provider's model accepts a speed setting. Feature-detected per
    /// model, never assumed. Default: supported. When false, a speed retake omits
    /// the setting and the attempt is logged as unsupported.
    /// </summary>
    bool SupportsSpeedSetting(string modelId) => true;

    /// <summary>
    /// Whether the provider's model honors bracket audio tags (e.g. [warmly]) as
    /// delivery control rather than speaking them. Feature-detected per model,
    /// never assumed. Default: not supported.
    /// </summary>
    bool SupportsEmotionTags(string modelId) => false;

    /// <summary>Declared PCM parameters for raw (non-WAV-container) synthesis output.</summary>
    int PcmSampleRate { get; }

    int PcmChannels { get; }

    int PcmBitsPerSample { get; }
}
