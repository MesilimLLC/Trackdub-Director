using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class EmotionTagTests
{
    private const string PassJson = """{"verdict":"pass","issues":[],"explanation":"Meaning preserved."}""";

    private sealed class HandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private static MetaClient BuildMeta()
    {
        var handler = new HandlerStub(async (request, cancellationToken) =>
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string content = body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal) ? PassJson : "Hola, Ashley.";
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
        });
        return new MetaClient(new HttpClient(handler));
    }

    private sealed class FakeTagTts(bool supportsTags) : ITtsProvider
    {
        public List<string> Texts { get; } = [];

        public int PcmSampleRate => 16000;

        public int PcmChannels => 1;

        public int PcmBitsPerSample => 16;

        public bool SupportsEmotionTags(string modelId) => supportsTags;

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

        public Task<TtsSynthesisResult> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken cancellationToken = default)
        {
            Texts.Add(request.Text);
            // 1.0 s of audio at any text, so timing passes the 2.0 s window.
            byte[] wav = AudioDuration.ToWav(new byte[16000 * 2], new PcmFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 });
            return Task.FromResult(new TtsSynthesisResult(wav, request.Text.Length));
        }
    }

    private sealed record TagScenario(EmotionTagOptions Options, string? Label);

    private static async Task<AuditionReport> RunAsync(FakeTagTts tts, TagScenario scenario)
    {
        string? previous = Environment.GetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv);
        Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, "test-fake-key");
        try
        {
            var service = new AuditionService(BuildMeta(), tts, QaOptions.Default, new AuditionRuntimeOptions(EmotionTags: scenario.Options));
            return await service.RunAsync(
                [new AuditionSegmentInput("seg-1", "Hi Ashley!", 2.0, "anna", scenario.Label)], "en", "es");
        }
        finally
        {
            Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, previous);
        }
    }

    [Fact]
    public async Task Tag_is_prepended_at_synthesis_only_on_tag_honoring_models()
    {
        var tts = new FakeTagTts(supportsTags: true);
        AuditionReport report = await RunAsync(tts, new TagScenario(new EmotionTagOptions(Enabled: true), "cheerful"));

        AuditionTakeReport take = Assert.Single(report.Segments[0].Takes, t => t.Synthesized);
        Assert.Equal("[cheerfully]", take.EmotionTag);
        Assert.Equal("[cheerfully] Hola, Ashley.", tts.Texts[0]);
        // The gated text is byte-identical: no tag in the take text.
        Assert.Equal("Hola, Ashley.", take.Text);
        Assert.Equal(MeaningVerdict.Pass, take.MeaningVerdict);
    }

    [Fact]
    public async Task Tag_is_never_sent_to_a_model_that_would_speak_it()
    {
        var tts = new FakeTagTts(supportsTags: false);
        AuditionReport report = await RunAsync(tts, new TagScenario(new EmotionTagOptions(Enabled: true), "cheerful"));

        AuditionTakeReport take = Assert.Single(report.Segments[0].Takes, t => t.Synthesized);
        Assert.Null(take.EmotionTag);
        Assert.Equal("Hola, Ashley.", tts.Texts[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("neutral")]
    [InlineData("contemplative")] // unmappable: never invent a tag
    public async Task No_tag_without_a_mappable_label(string? label)
    {
        var tts = new FakeTagTts(supportsTags: true);
        AuditionReport report = await RunAsync(tts, new TagScenario(new EmotionTagOptions(Enabled: true), label));

        AuditionTakeReport take = Assert.Single(report.Segments[0].Takes, t => t.Synthesized);
        Assert.Null(take.EmotionTag);
        Assert.Equal("Hola, Ashley.", tts.Texts[0]);
    }

    [Fact]
    public async Task Default_applies_tags_since_adoption_and_disabled_does_not()
    {
        var first = new FakeTagTts(supportsTags: true);
        AuditionReport disabled = await RunAsync(first, new TagScenario(EmotionTagOptions.Disabled, "cheerful"));
        Assert.All(disabled.Segments[0].Takes, t => Assert.Null(t.EmotionTag));

        var second = new FakeTagTts(supportsTags: true);
        string? previous = Environment.GetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv);
        Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, "test-fake-key");
        AuditionReport baseline;
        try
        {
            baseline = await new AuditionService(BuildMeta(), second, QaOptions.Default)
                .RunAsync([new AuditionSegmentInput("seg-1", "Hi Ashley!", 2.0, "anna", "cheerful")], "en", "es");
        }
        finally
        {
            Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, previous);
        }

        AuditionTakeReport baselineTake = Assert.Single(baseline.Segments[0].Takes, t => t.Synthesized);
        Assert.Equal("[cheerfully]", baselineTake.EmotionTag);
    }

    [Theory]
    [InlineData("cheerful", "[cheerfully]")]
    [InlineData("Warm, friendly tone", "[warmly]")]
    [InlineData("surprised realization", "[surprised]")]
    [InlineData("A curious question", "[curiously]")]
    [InlineData("sheepish", "[sheepishly]")]
    [InlineData("melancholy", "[melancholically]")]
    [InlineData("deadpan delivery", "[deadpan]")]
    public void Label_mapping_covers_probe_labels(string label, string expected)
    {
        Assert.Equal(expected, EmotionTagMap.ForLabel(label));
    }

    [Fact]
    public void Emotion_and_pacing_keywords_combine()
    {
        Assert.Equal("[warmly] [slowly]", EmotionTagMap.ForLabel("warm, unhurried"));
        Assert.Equal("[pauses]", EmotionTagMap.ForLabel("with long pauses"));
        Assert.Empty(EmotionTagMap.TagsForLabel("contemplative"));
    }

    [Theory]
    [InlineData("eleven_v3", true)]
    [InlineData("eleven_v4", true)]
    [InlineData("eleven_v4_turbo", true)]
    [InlineData("eleven_multilingual_v2", false)]
    [InlineData("eleven_flash_v2_5", false)]
    public void ElevenLabs_tag_support_is_v3_and_v4_only(string modelId, bool expected)
    {
        using var provider = new ElevenLabsTtsProvider(new HttpClient(), modelId);
        Assert.Equal(expected, provider.SupportsEmotionTags(modelId));
    }
}
