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

    private sealed record TagScenario(EmotionTagOptions Options)
    {
        public string? Label { get; init; }
    }

    private sealed record ModelCase
    {
        public string ModelId { get; init; } = string.Empty;

        public bool Tagged { get; init; }
    }

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
        AuditionReport report = await RunAsync(tts, new TagScenario(new EmotionTagOptions(Enabled: true)) { Label = "cheerful" });

        AuditionTakeReport take = Assert.Single(report.Segments[0].Takes, t => t.Synthesized);
        Assert.Equal("[cheerful]", take.EmotionTag);
        Assert.Equal("[cheerful] Hola, Ashley.", tts.Texts[0]);
        // The gated text is byte-identical: no tag in the take text.
        Assert.Equal("Hola, Ashley.", take.Text);
        Assert.Equal(MeaningVerdict.Pass, take.MeaningVerdict);
    }

    [Fact]
    public async Task Tag_is_never_sent_to_a_model_that_would_speak_it()
    {
        var tts = new FakeTagTts(supportsTags: false);
        AuditionReport report = await RunAsync(tts, new TagScenario(new EmotionTagOptions(Enabled: true)) { Label = "cheerful" });

        AuditionTakeReport take = Assert.Single(report.Segments[0].Takes, t => t.Synthesized);
        Assert.Null(take.EmotionTag);
        Assert.Equal("Hola, Ashley.", tts.Texts[0]);
    }

    [Fact]
    public async Task No_tag_without_a_label()
    {
        string?[] labels = [null, "", "   "];
        foreach (string? label in labels)
        {
            var tts = new FakeTagTts(supportsTags: true);
            AuditionReport report = await RunAsync(tts, new TagScenario(new EmotionTagOptions(Enabled: true)) { Label = label });

            AuditionTakeReport take = Assert.Single(report.Segments[0].Takes, t => t.Synthesized);
            Assert.Null(take.EmotionTag);
            Assert.Equal("Hola, Ashley.", tts.Texts[0]);
        }
    }

    [Fact]
    public async Task Default_applies_tags_since_adoption_and_disabled_does_not()
    {
        var first = new FakeTagTts(supportsTags: true);
        AuditionReport disabled = await RunAsync(first, new TagScenario(EmotionTagOptions.Disabled) { Label = "cheerful" });
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
        Assert.Equal("[cheerful]", baselineTake.EmotionTag);
    }

    [Fact]
    public void Labels_pass_through_in_their_own_words()
    {
        (string Label, string Expected)[] cases =
        [
            ("cheerful", "[cheerful]"),
            ("Warm, friendly tone", "[Warm, friendly tone]"),
            ("surprised realization", "[surprised realization]"),
            ("A curious question", "[A curious question]"),
            ("sheepish", "[sheepish]"),
            ("melancholy", "[melancholy]"),
            ("deadpan delivery", "[deadpan delivery]"),
        ];

        foreach ((string label, string expected) in cases)
        {
            Assert.Equal(expected, EmotionTagMap.ForLabel(label));
        }
    }

    [Fact]
    public void Combined_label_passes_through_as_one_grouped_tag()
    {
        Assert.Equal("[warm, unhurried]", EmotionTagMap.ForLabel("warm, unhurried"));
        Assert.Equal("[with long pauses]", EmotionTagMap.ForLabel("with long pauses"));
        Assert.Equal("[contemplative]", EmotionTagMap.ForLabel("contemplative"));
    }

    [Fact]
    public void Sanitization_is_mechanical_only()
    {
        Assert.Equal("[cheerful]", EmotionTagMap.ForLabel("[cheerful]"));
        Assert.Equal("[warm and friendly]", EmotionTagMap.ForLabel("  warm\n  and   friendly "));
        Assert.Equal("[not happy]", EmotionTagMap.ForLabel("not happy"));
        string[] labels = ["", "   ", "[]"];
        foreach (string label in labels)
        {
            Assert.Null(EmotionTagMap.ForLabel(label));
        }
    }

    [Fact]
    public void Neutral_and_plain_labels_pass_through_too()
    {
        Assert.Equal("[neutral, brisk]", EmotionTagMap.ForLabel("neutral, brisk"));
    }

    [Fact]
    public async Task Pacing_label_from_segment_input_is_prepended_with_emotion()
    {
        var tts = new FakeTagTts(supportsTags: true);
        string? previous = Environment.GetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv);
        Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, "test-fake-key");
        AuditionReport report;
        try
        {
            var service = new AuditionService(BuildMeta(), tts, QaOptions.Default, new AuditionRuntimeOptions(EmotionTags: new EmotionTagOptions(Enabled: true)));
            report = await service.RunAsync(
                [new AuditionSegmentInput("seg-1", "Hi Ashley!", 2.0, "anna", "cheerful", "unhurried")], "en", "es");
        }
        finally
        {
            Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, previous);
        }

        AuditionTakeReport take = Assert.Single(report.Segments[0].Takes, t => t.Synthesized);
        Assert.Equal("[cheerful, unhurried]", take.EmotionTag);
        Assert.Equal("[cheerful, unhurried] Hola, Ashley.", tts.Texts[0]);
        // The gated text is byte-identical: no tag in the take text.
        Assert.Equal("Hola, Ashley.", take.Text);
    }

    private static async Task AssertModelTagAsync(ModelCase modelCase)
    {
        var handler = new HandlerStub(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                const string voices = """{"voices":[{"voice_id":"voice-alba","name":"Alba","category":"premade","labels":{"gender":"female"},"high_quality_base_model_ids":["eleven_multilingual_v2"]},{"voice_id":"voice-zoe","name":"Zoe","category":"premade","labels":{"gender":"female"},"high_quality_base_model_ids":["eleven_multilingual_v2"]}]}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(voices, Encoding.UTF8, "application/json"),
                };
            }

            // 1.0 s of raw PCM mono 16-bit at 16 kHz, accepted as audio by the provider.
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[16000 * 2]) };
        });
        using var provider = new ElevenLabsTtsProvider(new HttpClient(handler), modelCase.ModelId);

        string? previousMeta = Environment.GetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv);
        string? previousEleven = Environment.GetEnvironmentVariable(ElevenLabsAuth.ApiKeyEnv);
        Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, "test-fake-key");
        Environment.SetEnvironmentVariable(ElevenLabsAuth.ApiKeyEnv, "test-eleven-key");
        try
        {
            var service = new AuditionService(BuildMeta(), provider, QaOptions.Default, new AuditionRuntimeOptions(EmotionTags: new EmotionTagOptions(Enabled: true)));
            AuditionReport report = await service.RunAsync(
                [new AuditionSegmentInput("seg-1", "Hi Ashley!", 2.0, "anna", "cheerful")], "en", "es");

            AuditionTakeReport take = Assert.Single(report.Segments[0].Takes, t => t.Synthesized);
            if (modelCase.Tagged)
            {
                Assert.Equal("[cheerful]", take.EmotionTag);
            }
            else
            {
                Assert.Null(take.EmotionTag);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, previousMeta);
            Environment.SetEnvironmentVariable(ElevenLabsAuth.ApiKeyEnv, previousEleven);
        }
    }

    [Fact]
    public async Task Real_provider_model_id_decides_tag_at_synthesis()
    {
        ModelCase[] cases =
        [
            new() { ModelId = "eleven_v3", Tagged = true },
            new() { ModelId = "eleven_multilingual_v2", Tagged = false },
        ];

        foreach (ModelCase modelCase in cases)
        {
            await AssertModelTagAsync(modelCase);
        }
    }

    [Fact]
    public void ElevenLabs_tag_support_is_v3_and_v4_only()
    {
        (string ModelId, bool Expected)[] cases =
        [
            ("eleven_v3", true),
            ("eleven_v4", true),
            ("eleven_v4_turbo", false),
            ("eleven_multilingual_v2", false),
            ("eleven_flash_v2_5", false),
        ];

        foreach ((string modelId, bool expected) in cases)
        {
            using var provider = new ElevenLabsTtsProvider(new HttpClient(), modelId);
            Assert.Equal(expected, provider.SupportsEmotionTags(modelId));
        }
    }
}
