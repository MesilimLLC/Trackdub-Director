using System.Text.Json;

namespace Trackdub.Director.Core;

/// <summary>
/// Transcribe stage: WAV audio in, segments out. Thin over one
/// <see cref="IMuseTranscribeClient"/> call; maps each diarization turn to the
/// app's input shape ({id, text, duration_seconds}) with the speaker label
/// recorded verbatim ("A"/"B", never mapped to a character name). Segment ids are
/// sequential (seg-001, seg-002, ...) in turn order. DurationSeconds is the turn's
/// endMs minus startMs, rounded to 0.1 s (the voa10 stamping convention). Usage is
/// always null: the endpoint reports no token usage (billed per audio hour).
/// </summary>
public sealed class TranscribeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IMuseTranscribeClient _client;

    public TranscribeService(IMuseTranscribeClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public TranscribeService()
        : this(new MuseTranscribeClient())
    {
    }

    public TranscribeService(string rawCapturePath)
        : this(new MuseTranscribeClient(null, rawCapturePath))
    {
    }

    public async Task<TranscribeResult> TranscribeAsync(Stream wavAudio, string? fileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wavAudio);

        TranscribeRawResponse raw = await _client.TranscribeAsync(wavAudio, fileName, cancellationToken).ConfigureAwait(false);
        return Map(raw);
    }

    public async Task<TranscribeResult> TranscribeFileAsync(string wavPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        string resolved = Path.GetFullPath(wavPath);
        if (!File.Exists(resolved))
        {
            throw new InvalidOperationException($"Audio file not found: {resolved}");
        }

        await using FileStream stream = File.OpenRead(resolved);
        return await TranscribeAsync(stream, Path.GetFileName(resolved), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps a raw response to a result. Exposed for tests and for callers that already hold a raw response.</summary>
    public TranscribeResult Map(TranscribeRawResponse raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        // Skip blank / whitespace-only transcripts (non-speech turns). IDs are
        // assigned after filtering so they stay contiguous for downstream lanes.
        var segments = new List<TranscribeSegment>(raw.Turns.Length);
        foreach (TranscribeTurn turn in raw.Turns)
        {
            string text = turn.Transcript.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            double durationSeconds = Math.Round((turn.EndMs - turn.StartMs) / 1000.0, 1, MidpointRounding.AwayFromZero);
            segments.Add(new TranscribeSegment(
                $"seg-{segments.Count + 1:000}",
                text,
                durationSeconds,
                turn.Speaker));
        }

        return new TranscribeResult(
            MetaAuth.GetTranscribeModel(),
            GetEndpointHost(),
            DateTime.UtcNow.ToString("O"),
            raw.SessionId,
            raw.Transcript,
            raw.AudioDurationMs,
            segments.ToArray(),
            raw.Turns,
            Usage: null);
    }

    public async Task WriteSegmentsAsync(string outputPath, TranscribeResult result, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(result);

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(result.Segments, JsonOptions);
        await File.WriteAllTextAsync(outputPath, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteResultAsync(string outputPath, TranscribeResult result, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(result);

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(result, JsonOptions);
        await File.WriteAllTextAsync(outputPath, json, cancellationToken).ConfigureAwait(false);
    }

    private static string GetEndpointHost()
    {
        string baseUrl = MetaAuth.GetBaseUrl();
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
        {
            return uri.Host;
        }

        return baseUrl;
    }
}
