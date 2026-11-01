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

        string requestJson = JsonSerializer.Serialize(new
        {
            mode = "DIARIZATION",
            model = MetaAuth.GetTranscribeModel(),
            audioEncoding = "WAV",
        }, JsonOptions);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(requestJson, Encoding.UTF8, "application/json"), "request");
        var audioContent = new StreamContent(wavAudio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audioContent, "audio", string.IsNullOrWhiteSpace(fileName) ? "audio.wav" : fileName);

        // Create the raw-capture parent before the paid call so a missing folder
        // fails closed before spend, and the response can still be written after.
        if (!string.IsNullOrWhiteSpace(_rawCapturePath))
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(_rawCapturePath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{MetaAuth.GetBaseUrl()}/asr/transcribe")
        {
            Content = form,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MetaAuth.GetApiKey());

        string body = await SendForBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(_rawCapturePath))
        {
            await File.WriteAllTextAsync(_rawCapturePath, body, cancellationToken).ConfigureAwait(false);
        }

        return Parse(body);
    }

    private async Task<string> SendForBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Headers-read keeps the timeout covering body read too only when the
        // whole send+read path shares the same cancellation / catch mapping.
        try
        {
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            string body = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new MetaApiException($"Meta Model API HTTP {(int)response.StatusCode}: {Truncate(body)}");
            }

            return body;
        }
        catch (HttpRequestException ex)
        {
            throw new MetaApiException($"Meta Model API request failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MetaApiException("Meta Model API request timed out.", ex);
        }
    }

    private static TranscribeRawResponse Parse(string body)
    {
        TranscribeRawResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TranscribeRawResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MetaApiException($"Failed to parse Meta Model API transcribe response: {Truncate(body)}", ex);
        }

        if (parsed is null || parsed.Transcript is null || parsed.Turns is null)
        {
            throw new MetaApiException($"Unexpected Meta Model API transcribe response shape: {Truncate(body)}");
        }

        foreach (TranscribeTurn turn in parsed.Turns)
        {
            if (turn is null || turn.Transcript is null || turn.EndMs < turn.StartMs)
            {
                throw new MetaApiException($"Unexpected Meta Model API transcribe turn shape: {Truncate(body)}");
            }
        }

        return parsed;
    }

    private static string Truncate(string body)
    {
        return body.Length > 500 ? body[..500] : body;
    }
}
