using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Trackdub.Director.Core;

/// <summary>
/// <see cref="IMuseTranscribeClient"/> over the Meta Model API. One multipart POST
/// per call: a "request" JSON part (mode DIARIZATION) and an "audio" WAV part,
/// the exact shape dataset prep probe-verified (samples/voa10/NOTES.md §3).
/// HttpClient + System.Text.Json only; auth via <see cref="MetaAuth"/> (env-only,
/// never logged).
/// </summary>
public sealed class MuseTranscribeClient : IMuseTranscribeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly string? _rawCapturePath;

    /// <param name="rawCapturePath">When set, the raw response body is written to
    /// this path before parsing, so a rejected shape is preserved for inspection
    /// without spending another call (dataset prep keeps raw responses the same way).</param>
    public MuseTranscribeClient(HttpClient? httpClient = null, string? rawCapturePath = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        _rawCapturePath = rawCapturePath;
    }

    public async Task<TranscribeRawResponse> TranscribeAsync(Stream wavAudio, string? fileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wavAudio);

        byte[] audioBytes = await ReadAudioAsync(wavAudio, cancellationToken).ConfigureAwait(false);
        var request = new TranscriptionRequest(audioBytes, BuildRequestJson(), fileName);
        EnsureCaptureDirectory();
        RawBody body = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await CaptureRawAsync(body, cancellationToken).ConfigureAwait(false);
        return Parse(body);
    }

    private static async Task<byte[]> ReadAudioAsync(Stream wavAudio, CancellationToken cancellationToken)
    {
        using var audioBuffer = new MemoryStream();
        await wavAudio.CopyToAsync(audioBuffer, cancellationToken).ConfigureAwait(false);
        return audioBuffer.ToArray();
    }

    private static string BuildRequestJson()
    {
        return JsonSerializer.Serialize(new
        {
            mode = "DIARIZATION",
            model = MetaAuth.GetTranscribeModel(),
            audioEncoding = "WAV",
        }, JsonOptions);
    }

    // Create the raw-capture parent before the paid call so a missing folder
    // fails closed before spend, and the response can still be written after.
    private void EnsureCaptureDirectory()
    {
        if (string.IsNullOrWhiteSpace(_rawCapturePath)) return;

        string? dir = Path.GetDirectoryName(Path.GetFullPath(_rawCapturePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private async Task<RawBody> SendAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using HttpRequestMessage httpRequest = CreateRequest(request);
            HttpResponseMessage response = await SendOnceAsync(httpRequest, cancellationToken).ConfigureAwait(false);

            if (ShouldRetryTranscriptionResponse(response.StatusCode, attempt))
            {
                await DelayAndDisposeAsync(response, attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new MetaApiException($"Meta transcription request failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MetaApiException("Meta transcription timed out; provider outcome is unknown, so it was not retried.", ex);
        }
    }

    private static async Task DelayAndDisposeAsync(HttpResponseMessage response, int attempt, CancellationToken cancellationToken)
    {
        TimeSpan delay = RetryDelay(response.Headers.RetryAfter, attempt);
        response.Dispose();
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RawBody> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            RawBody body;
            try
            {
                body = response.Content is null
                    ? new RawBody(string.Empty)
                    : new RawBody(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (HttpRequestException ex)
            {
                throw new MetaApiException("Meta transcription response ended unexpectedly; provider outcome is unknown, so it was not retried.", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MetaApiException("Meta transcription response timed out; provider outcome is unknown, so it was not retried.", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MetaApiException($"Meta transcription HTTP {(int)response.StatusCode}: {Truncate(body)}");
            }

            return body;
        }
    }

    private async Task CaptureRawAsync(RawBody body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_rawCapturePath)) return;

        await File.WriteAllTextAsync(_rawCapturePath, body.Value, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(TranscriptionRequest request)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(request.RequestJson, Encoding.UTF8, "application/json"), "request");
        var audioContent = new ByteArrayContent(request.AudioBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        string fileName = string.IsNullOrWhiteSpace(request.FileName) ? "audio.wav" : Path.GetFileName(request.FileName);
        form.Add(audioContent, "audio", fileName);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{MetaAuth.GetBaseUrl()}/asr/transcribe")
        {
            Content = form,
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MetaAuth.GetApiKey());
        return httpRequest;
    }

    private static TimeSpan RetryDelay(System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter, int attempt)
    {
        TimeSpan requested = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(250 * (1 << attempt)));
        double jitterMilliseconds = Random.Shared.Next(0, 251);
        return TimeSpan.FromMilliseconds(Math.Clamp(requested.TotalMilliseconds + jitterMilliseconds, 0, 5000));
    }

    private static bool ShouldRetryTranscriptionResponse(System.Net.HttpStatusCode statusCode, int attempt)
    {
        return (statusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 2)
            || (statusCode == System.Net.HttpStatusCode.InternalServerError && attempt < 1);
    }

    private static TranscribeRawResponse Parse(RawBody body)
    {
        TranscribeRawResponse parsed = Deserialize(body);
        if (parsed.Transcript is null)
        {
            throw new MetaApiException($"Unexpected Meta Model API transcribe response shape: {Truncate(body)}");
        }

        if (parsed.Turns is null)
        {
            throw new MetaApiException($"Unexpected Meta Model API transcribe response shape: {Truncate(body)}");
        }

        foreach (TranscribeTurn turn in parsed.Turns)
        {
            ValidateTurn(turn, body);
        }

        return parsed;
    }

    private static TranscribeRawResponse Deserialize(RawBody body)
    {
        TranscribeRawResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TranscribeRawResponse>(body.Value, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MetaApiException($"Failed to parse Meta Model API transcribe response: {Truncate(body)}", ex);
        }

        return parsed ?? throw new MetaApiException($"Unexpected Meta Model API transcribe response shape: {Truncate(body)}");
    }

    private static void ValidateTurn(TranscribeTurn turn, RawBody body)
    {
        if (turn is null)
        {
            throw new MetaApiException($"Unexpected Meta Model API transcribe turn shape: {Truncate(body)}");
        }

        if (turn.Transcript is null)
        {
            throw new MetaApiException($"Unexpected Meta Model API transcribe turn shape: {Truncate(body)}");
        }

        if (turn.EndMs < turn.StartMs)
        {
            throw new MetaApiException($"Unexpected Meta Model API transcribe turn shape: {Truncate(body)}");
        }
    }

    private static string Truncate(RawBody body)
    {
        return body.Value.Length > 500 ? body.Value[..500] : body.Value;
    }

    private sealed record TranscriptionRequest(byte[] AudioBytes, string RequestJson, string? FileName);

    private sealed record RawBody(string Value);
}
