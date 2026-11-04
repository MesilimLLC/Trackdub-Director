using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trackdub.Director.Core;
using static Trackdub.Director.Tests.ServerTestSupport;

namespace Trackdub.Director.Tests;

/// <summary>
/// Audition and transcribe endpoint tests.
/// </summary>
public sealed class AudioEndpointTests
{
    [Fact]
    public async Task Audition_without_elevenlabs_key_fails_closed_without_meta_calls()
    {
        using var auth = FakeAuth();
        using var elevenLabs = EnvShim(ElevenLabsAuth.ApiKeyEnv, "");
        int metaCalls = 0;
        using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) =>
        {
            metaCalls++;
            return Task.FromResult(ChatOk(new ChatReply("muse-spark-1.1", "Hola.")));
        }));
        using HttpClient client = factory.CreateClient();

        var body = new { segments = new[] { new { id = "seg-1", text = "Hello.", duration_seconds = 1.0, speaker_id = "anna" } } };
        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/audition", body);
        string json = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("No ElevenLabs API key", json);

        int calls = metaCalls;
        Assert.Equal(0, calls);
        Assert.DoesNotContain("test-fake-key", json.ToLowerInvariant());
    }

    private static HttpMessageHandler TranscribeHandler()
    {
        return new DelegatingHandlerStub((request, _) =>
        {
            string? path = request.RequestUri?.AbsolutePath;
            Assert.True(path is not null && path.EndsWith("/asr/transcribe", StringComparison.Ordinal), $"Expected /asr/transcribe, got {path}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CannedTranscribeBody, Encoding.UTF8, "application/json"),
            });
        });
    }

    private static MultipartFormDataContent AudioForm(byte[] wav)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(wav);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(content, "audio", "clip.wav");
        return form;
    }

    [Fact]
    public async Task Transcribe_happy_path_returns_segments_with_verbatim_speakers_and_null_usage()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(TranscribeHandler());
        using HttpClient client = factory.CreateClient();

        using var form = AudioForm([82, 73, 70, 70, 1, 0, 0, 0]);
        HttpResponseMessage resp = await client.PostAsync("/api/transcribe", form);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        JsonElement segments = root.GetProperty("segments");
        Assert.Equal(2, segments.GetArrayLength());

        JsonElement first = segments[0];
        ExpectStrings(first, ("id", "seg-001"), ("text", "Hi, Ashley."), ("speaker", "A"));
        Assert.Equal(1.2, first.GetProperty("duration_seconds").GetDouble());

        string? secondSpeaker = segments[1].GetProperty("speaker").GetString();
        Assert.Equal("B", secondSpeaker);

        // The server omits null properties (WhenWritingNull): usage absent means
        // not reported; usage_reported=false is the explicit marker. Never zero.
        bool hasUsage = root.TryGetProperty("usage", out _);
        bool usageReported = root.GetProperty("usage_reported").GetBoolean();
        Assert.False(hasUsage);
        Assert.False(usageReported);

        Assert.DoesNotContain("total_tokens", json, StringComparison.Ordinal);
        Assert.DoesNotContain("test-fake-key", json.ToLowerInvariant());
    }

    [Fact]
    public async Task Transcribe_rejects_a_non_multipart_request()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(TranscribeHandler());
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resp = await client.PostAsync("/api/transcribe", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Transcribe_rejects_a_form_without_audio()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(TranscribeHandler());
        using HttpClient client = factory.CreateClient();

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("x"), "other");
        HttpResponseMessage resp = await client.PostAsync("/api/transcribe", form);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
