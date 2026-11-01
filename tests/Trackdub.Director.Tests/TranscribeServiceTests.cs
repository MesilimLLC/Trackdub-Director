using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class TranscribeServiceTests
{
    // Canned diarization response in the probe-verified shape (samples/voa10-scale/fpc-transcribe-raw.json).
    private const string CannedBody = """{"sessionId":"voyager/transcribe/TEST123","transcript":"Hi, Ashley. Anna, which coffee shop?","audioDurationMs":6060,"turns":[{"turnId":0,"startMs":60,"endMs":1260,"transcript":"Hi, Ashley.","speaker":"A"},{"turnId":1,"startMs":1660,"endMs":6060,"transcript":"Anna, which coffee shop?","speaker":"B"}]}""";

    private sealed class FakeTranscribeClient(TranscribeRawResponse response) : IMuseTranscribeClient
    {
        public int Calls;
        public long BytesSeen;

        public Task<TranscribeRawResponse> TranscribeAsync(Stream wavAudio, string? fileName = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            BytesSeen += wavAudio.CanSeek ? wavAudio.Length : -1;
            return Task.FromResult(response);
        }
    }

    private static TranscribeRawResponse CannedRaw()
    {
        return JsonSerializer.Deserialize<TranscribeRawResponse>(CannedBody, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    [Fact]
    public void Blank_transcript_turns_are_skipped_and_ids_stay_contiguous()
    {
        var raw = new TranscribeRawResponse(
            "sess",
            "Hello",
            3000,
            [
                new TranscribeTurn(0, 0, 1000, "Hello", "A"),
                new TranscribeTurn(1, 1000, 2000, "   ", "B"),
                new TranscribeTurn(2, 2000, 3000, "World", "C"),
            ]);

        TranscribeResult result = new TranscribeService(new FakeTranscribeClient(raw)).Map(raw);

        Assert.Equal(2, result.Segments.Length);
        Assert.Equal("seg-001", result.Segments[0].Id);
        Assert.Equal("Hello", result.Segments[0].Text);
        Assert.Equal("seg-002", result.Segments[1].Id);
        Assert.Equal("World", result.Segments[1].Text);
        Assert.Equal("C", result.Segments[1].Speaker);
    }

    [Fact]
    public async Task Turns_map_to_segments_with_stamp_math_and_verbatim_speakers()
    {
        var fake = new FakeTranscribeClient(CannedRaw());
        var service = new TranscribeService(fake);

        TranscribeResult result = await service.TranscribeAsync(new MemoryStream([1, 2, 3]), "clip.wav");

        Assert.Equal(1, fake.Calls);
        Assert.Equal(2, result.Segments.Length);

        TranscribeSegment first = result.Segments[0];
        Assert.Equal("seg-001", first.Id);
        Assert.Equal("Hi, Ashley.", first.Text);
        Assert.Equal(1.2, first.DurationSeconds);
        Assert.Equal("A", first.Speaker);

        TranscribeSegment second = result.Segments[1];
        Assert.Equal("seg-002", second.Id);
        Assert.Equal(4.4, second.DurationSeconds);
        Assert.Equal("B", second.Speaker);
    }

    [Fact]
    public async Task Usage_is_null_and_reported_as_not_reported_never_zero()
    {
        var fake = new FakeTranscribeClient(CannedRaw());
        var service = new TranscribeService(fake);

        TranscribeResult result = await service.TranscribeAsync(new MemoryStream([1]), null);

        Assert.Null(result.Usage);
        Assert.False(result.UsageReported);

        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("usage").ValueKind);
        Assert.False(doc.RootElement.GetProperty("usage_reported").GetBoolean());
    }

    [Fact]
    public async Task Result_carries_session_transcript_and_raw_turns()
    {
        var fake = new FakeTranscribeClient(CannedRaw());
        var service = new TranscribeService(fake);

        TranscribeResult result = await service.TranscribeAsync(new MemoryStream([1]), null);

        Assert.Equal("voyager/transcribe/TEST123", result.SessionId);
        Assert.Equal(6060, result.AudioDurationMs);
        Assert.Equal(2, result.Turns.Length);
        Assert.Equal(1660, result.Turns[1].StartMs);
        Assert.Equal(MetaAuth.DefaultTranscribeModel, result.Model);
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private static IDisposable EnvShim(string key, string value)
    {
        string? prev = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
        return new Restore(key, prev);
    }

    private sealed class Restore(string key, string? prev) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(key, prev);
    }

    [Fact]
    public async Task Client_posts_multipart_diarization_request_and_parses_turns()
    {
        using var key = EnvShim(MetaAuth.PrimaryApiKeyEnv, "test-fake-key");
        using var model = EnvShim(MetaAuth.TranscribeModelEnv, "");
        string? seenPath = null;
        string? seenBody = null;
        string? seenContentType = null;
        var handler = new DelegatingHandlerStub(async (request, ct) =>
        {
            seenPath = request.RequestUri?.AbsolutePath;
            seenContentType = request.Content?.Headers.ContentType?.MediaType;
            seenBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CannedBody, Encoding.UTF8, "application/json"),
            };
        });
        var client = new MuseTranscribeClient(new HttpClient(handler));

        TranscribeRawResponse raw = await client.TranscribeAsync(new MemoryStream([82, 73, 70, 70]), "clip.wav");

        Assert.Equal("/v1/asr/transcribe", seenPath);
        Assert.Equal("multipart/form-data", seenContentType);
        Assert.NotNull(seenBody);
        Assert.Contains("\"mode\":\"DIARIZATION\"", seenBody, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"muse-voice-transcribe-1.0\"", seenBody, StringComparison.Ordinal);
        Assert.Contains("\"audioEncoding\":\"WAV\"", seenBody, StringComparison.Ordinal);
        Assert.Equal(2, raw.Turns.Length);
        Assert.Equal("B", raw.Turns[1].Speaker);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"transcript":"x"}""")]
    [InlineData("""{"transcript":"x","turns":[{"turnId":0,"startMs":10,"endMs":5,"transcript":"a","speaker":"A"}]}""")]
    public async Task Client_fails_closed_on_malformed_responses(string body)
    {
        using var key = EnvShim(MetaAuth.PrimaryApiKeyEnv, "test-fake-key");
        var handler = new DelegatingHandlerStub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));
        var client = new MuseTranscribeClient(new HttpClient(handler));

        await Assert.ThrowsAsync<MetaApiException>(() => client.TranscribeAsync(new MemoryStream([1]), null));
    }

    [Fact]
    public async Task Client_surfaces_http_error_status()
    {
        using var key = EnvShim(MetaAuth.PrimaryApiKeyEnv, "test-fake-key");
        var handler = new DelegatingHandlerStub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"bad audio\"}", Encoding.UTF8, "application/json"),
        }));
        var client = new MuseTranscribeClient(new HttpClient(handler));

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.TranscribeAsync(new MemoryStream([1]), null));
        Assert.Contains("HTTP 400", ex.Message, StringComparison.Ordinal);
    }
}
