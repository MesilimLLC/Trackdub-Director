using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class VoiceCompatibilityTests
{
    private const string PassJson = """{"verdict":"pass","issues":[],"explanation":"Meaning preserved."}""";

    private sealed class HandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private static HttpResponseMessage Chat(string content)
    {
        var payload = new
        {
            id = "chatcmpl-test",
            @object = "chat.completion",
            created = 1234567890L,
            model = "muse-spark-1.1",
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
            usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>A voice advertising only the v2 family, as Alice/Bella do live.</summary>
    private sealed class V2OnlyVoiceTts : ITtsProvider
    {
        public int PcmSampleRate => 16000;

        public int PcmChannels => 1;

        public int PcmBitsPerSample => 16;

        public void EnsureConfigured()
        {
        }

        public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TtsVoice>>(
            [
                new TtsVoice("voice-alba", "Alba", "premade", "female", ["eleven_multilingual_v2"]),
                new TtsVoice("voice-zoe", "Zoe", "premade", "female", ["eleven_multilingual_v2"]),
            ]);
        }

        public Task<TtsSynthesisResult> SynthesizeAsync(string voiceId, string text, string languageCode, double? speed = null, CancellationToken cancellationToken = default)
        {
            byte[] wav = AudioDuration.ToWav(new byte[16000 * 2], new PcmFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 });
            return Task.FromResult(new TtsSynthesisResult(wav, text.Length));
        }
    }

    [Fact]
    public async Task Voice_advertising_only_v2_is_cast_and_synthesized_under_the_v4_default()
    {
        var metaHandler = new HandlerStub(async (request, cancellationToken) =>
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Chat(body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal) ? PassJson : "Hola, Ashley.");
        });

        string? previous = Environment.GetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv);
        Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, "test-fake-key");
        try
        {
            var service = new AuditionService(new MetaClient(new HttpClient(metaHandler)), new V2OnlyVoiceTts(), QaOptions.Default);
            AuditionReport report = await service.RunAsync(
                [new AuditionSegmentInput("seg-1", "Hi Ashley!", 2.0, "anna")], "en", "es");

            AuditionSegmentReport line = Assert.Single(report.Segments);
            Assert.True(line.Cast);
            Assert.NotNull(line.WinnerTakeNumber);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, previous);
        }
    }

    [Fact]
    public async Task Provider_sends_its_configured_model_regardless_of_voice_advertised_models()
    {
        string? requestBody = null;
        var handler = new HandlerStub(async (request, cancellationToken) =>
        {
            requestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[3200]) };
        });

        string? previousKey = Environment.GetEnvironmentVariable(ElevenLabsAuth.ApiKeyEnv);
        Environment.SetEnvironmentVariable(ElevenLabsAuth.ApiKeyEnv, "test-fake-key");
        try
        {
            using var provider = new ElevenLabsTtsProvider(new HttpClient(handler));
            Assert.Equal("eleven_v4", provider.ModelId);

            // The voice id is all synthesis takes from the voice; its advertised
            // model list (high_quality_base_model_ids) is never consulted here.
            await provider.SynthesizeAsync("voice-that-advertises-v2-only", "Hola.", "es");

            using JsonDocument doc = JsonDocument.Parse(requestBody!);
            Assert.Equal("eleven_v4", doc.RootElement.GetProperty("model_id").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ElevenLabsAuth.ApiKeyEnv, previousKey);
        }
    }
}
