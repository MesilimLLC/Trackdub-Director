using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class AuditionServiceTests
{
    private const string PassJson = """{"verdict":"pass","issues":[],"explanation":"Meaning preserved."}""";

    private static string FailJson(string issue) => $$"""{"verdict":"fail","issues":["{{issue}}"],"explanation":"Candidate omits material meaning."}""";

    private sealed class ChatReply
    {
        public required string Content { get; init; }

        public int PromptTokens { get; init; } = 10;

        public int CompletionTokens { get; init; } = 5;
    }

    private static HttpResponseMessage ChatResponse(ChatReply reply)
    {
        var payload = new
        {
            id = $"chatcmpl-test-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = 1234567890L,
            model = "muse-spark-1.1",
            choices = new[]
            {
                new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = reply.Content } },
            },
            usage = new
            {
                prompt_tokens = reply.PromptTokens,
                completion_tokens = reply.CompletionTokens,
                total_tokens = reply.PromptTokens + reply.CompletionTokens,
            },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }

    private sealed class EnvVar
    {
        public required string Key { get; init; }

        public string? Value { get; init; }
    }

    private static EnvVar MetaKey => new() { Key = MetaAuth.PrimaryApiKeyEnv, Value = "LLM_test-fake-key" };

    private static EnvVar NoElevenLabsKey => new() { Key = ElevenLabsAuth.ApiKeyEnv, Value = "" };

    private static IDisposable EnvShim(EnvVar variable)
    {
        string? previous = Environment.GetEnvironmentVariable(variable.Key);
        Environment.SetEnvironmentVariable(variable.Key, variable.Value);
        return new Restore(new EnvVar { Key = variable.Key, Value = previous });
    }

    private sealed class Restore(EnvVar snapshot) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(snapshot.Key, snapshot.Value);
    }

    /// <summary>Fake TTS: deterministic WAV bytes of queued durations; records every call.</summary>
    private sealed class FakeTts : ITtsProvider
    {
        private readonly Queue<double> _durations;

        public FakeTts(IEnumerable<double> durations, IEnumerable<TtsVoice>? voices = null)
        {
            _durations = new Queue<double>(durations);
            Voices = voices?.ToList() ?? [.. DefaultVoices()];
        }

        public List<TtsVoice> Voices { get; } = [];

        public List<(string VoiceId, string Text)> SynthCalls { get; } = [];

        public List<double?> SynthSpeeds { get; } = [];

        public int PcmSampleRate => 16000;

        public int PcmChannels => 1;

        public int PcmBitsPerSample => 16;

        public PcmFormat Format => new()
        {
            SampleRate = PcmSampleRate,
            Channels = PcmChannels,
            BitsPerSample = PcmBitsPerSample,
        };

        public void EnsureConfigured()
        {
        }

        public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TtsVoice>>(Voices.ToArray());
        }

        public Task<TtsSynthesisResult> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken cancellationToken = default)
        {
            SynthCalls.Add((request.VoiceId, request.Text));
            SynthSpeeds.Add(request.Speed);
            double seconds = _durations.Count > 0 ? _durations.Dequeue() : 1.0;
            int frames = (int)Math.Round(seconds * PcmSampleRate);
            byte[] pcm = new byte[frames * 2];
            byte[] wav = AudioDuration.ToWav(pcm, Format);
            return Task.FromResult(new TtsSynthesisResult(wav, request.Text.Length));
        }

        public static TtsVoice[] DefaultVoices()
        {
            return
            [
                new TtsVoice("male-1", "Adam", "premade", "male", ["eleven_multilingual_v2"]),
                new TtsVoice("cloned-1", "Clone Carla", "cloned", "female", ["eleven_multilingual_v2"]),
                new TtsVoice("turbo-1", "Turbo Ana", "premade", "female", ["eleven_turbo_v2"]),
                new TtsVoice("voice-zoe", "Zoe", "premade", "female", ["eleven_multilingual_v2"]),
                new TtsVoice("voice-alba", "Alba", "premade", "female", ["eleven_multilingual_v2"]),
            ];
        }
    }

    /// <summary>Counting fake Meta handler: translations, variants JSONs, and meaning JSONs from queues.</summary>
    private sealed class MetaFake
    {
        public Queue<string> Translations = new();
        public Queue<string> VariantsJsons = new();
        public Queue<string> MeaningJsons = new();
        public int Calls;
        public int VariantsCalls;

        public MetaClient BuildClient()
        {
            var handler = new DelegatingHandlerStub(HandleAsync);
            return new MetaClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) });
        }

        private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal))
            {
                return MeaningResponse();
            }

            if (body.Contains("Alternates wanted", StringComparison.Ordinal))
            {
                return VariantsResponse();
            }

            return TranslationResponse();
        }

        private HttpResponseMessage MeaningResponse()
        {
            if (MeaningJsons.Count == 0)
            {
                throw new InvalidOperationException("No more canned meaning JSON queued.");
            }

            return ChatResponse(new ChatReply
            {
                Content = MeaningJsons.Dequeue(),
                PromptTokens = 7,
                CompletionTokens = 4,
            });
        }

        private HttpResponseMessage VariantsResponse()
        {
            VariantsCalls++;
            if (VariantsJsons.Count == 0)
            {
                throw new InvalidOperationException("No more canned variants JSON queued.");
            }

            return ChatResponse(new ChatReply { Content = VariantsJsons.Dequeue() });
        }

        private HttpResponseMessage TranslationResponse()
        {
            if (Translations.Count == 0)
            {
                throw new InvalidOperationException("No more canned translations queued.");
            }

            return ChatResponse(new ChatReply { Content = Translations.Dequeue() });
        }
    }

    private static AuditionService BuildService(MetaFake meta, FakeTts tts)
    {
        return new AuditionService(meta.BuildClient(), tts, QaOptions.Default);
    }

    [Fact]
    public async Task MeaningFail_texts_are_never_synthesized()
    {
        using var shim = EnvShim(MetaKey);
        var meta = new MetaFake();
        meta.Translations.Enqueue("Una toma mas.");
        meta.Translations.Enqueue("Necesitamos una toma mas.");
        meta.MeaningJsons.Enqueue(FailJson("dropped subject Necesitamos"));
        meta.MeaningJsons.Enqueue(PassJson);
        var tts = new FakeTts([1.0]);

        AuditionService service = BuildService(meta, tts);
        AuditionReport report = await service.RunAsync(
            [new AuditionSegmentInput("seg-1", "We need one more take.", 2.0, "anna")], "en", "es");

        // Casting picks the two eligible stock voices in name order.
        Assert.Equal(
            ("Alba", "Zoe"),
            (report.Cast.Single(c => c.SpeakerId == "anna").VoiceName, report.Cast.Single(c => c.SpeakerId == "ashley").VoiceName));

        // Only the meaning-passing revision was synthesized (call counts prove the gate).
        AuditionSegmentReport line = Assert.Single(report.Segments);
        Assert.Equal(2, line.Takes.Length);

        AuditionTakeReport failed = line.Takes[0];
        Assert.Equal((MeaningVerdict.Fail, false), (failed.MeaningVerdict, failed.Synthesized));

        AuditionTakeReport passed = line.Takes[1];
        Assert.Equal((MeaningVerdict.Pass, true), (passed.MeaningVerdict, passed.Synthesized));

        Assert.Equal(
            (1, "Necesitamos una toma mas.", "voice-alba"),
            (tts.SynthCalls.Count, tts.SynthCalls[0].Text, tts.SynthCalls[0].VoiceId));
        Assert.True(line.Cast);
    }

    private sealed class SeedText
    {
        public required string Value { get; init; }

        public required int Length { get; init; }
    }

    private static string Long(SeedText seed)
    {
        // Overlong for the QA estimator (window 4.0s allows 66 chars), so QA
        // revision loop produces several candidates with meaning NotEvaluated.
        var sb = new StringBuilder(seed.Value);
        while (sb.Length < seed.Length)
        {
            sb.Append(seed.Value);
        }

        return sb.ToString()[..seed.Length];
    }

    private enum MeaningPasses
    {
        Three = 3,
    }

    private static MetaFake OverlongMeta(MeaningPasses passes)
    {
        var meta = new MetaFake();
        meta.Translations.Enqueue(Long(new SeedText { Value = "Frase larga numero uno que sigue. ", Length = 100 }));
        meta.Translations.Enqueue(Long(new SeedText { Value = "Frase larga numero dos que sigue. ", Length = 100 }));
        meta.Translations.Enqueue(Long(new SeedText { Value = "Frase larga numero tres que sigue. ", Length = 100 }));
        meta.Translations.Enqueue(Long(new SeedText { Value = "Frase larga numero cuatro que sigue. ", Length = 100 }));
        for (int i = 0; i < (int)passes; i++)
        {
            meta.MeaningJsons.Enqueue(PassJson);
        }

        return meta;
    }

    [Fact]
    public async Task BestFit_take_wins_among_passing_takes()
    {
        using var shim = EnvShim(MetaKey);
        MetaFake meta = OverlongMeta(MeaningPasses.Three);
        // Window 4.0 (allowed 4.4): 5.0 fails timing; 4.3 passes (err 0.3); 4.05 passes (err 0.05) and wins.
        var tts = new FakeTts([5.0, 4.3, 4.05]);

        AuditionReport report = await BuildService(meta, tts).RunAsync(
            [new AuditionSegmentInput("seg-1", "A long line that runs over.", 4.0, "ashley")], "en", "es");

        AuditionSegmentReport line = Assert.Single(report.Segments);
        Assert.Equal(3, line.Takes.Length);
        Assert.Equal(3, tts.SynthCalls.Count);

        AuditionTakeReport[] takes = line.Takes;
        Assert.Equal(AuditionTimingVerdict.Fail, takes[0].TimingVerdict);
        Assert.Equal(AuditionTimingVerdict.Pass, takes[1].TimingVerdict);
        Assert.Equal(AuditionTimingVerdict.Pass, takes[2].TimingVerdict);

        AuditionTakeReport winner = takes[2];
        Assert.Equal(3, line.WinnerTakeNumber);
        Assert.Equal("seg-1-take3.wav", line.WinnerFileName);
        Assert.True(winner.IsWinner);

        AuditionTakeReport runnerUp = takes[1];
        Assert.False(runnerUp.IsWinner);
        Assert.True(line.Cast);
        Assert.Equal(1, report.Totals.LinesCast);

        AuditionTotals totals = report.Totals;
        Assert.Equal(3, totals.TakesSynthesized);
        Assert.Equal(7, totals.MetaCalls);
    }

    [Fact]
    public async Task Writer_keeps_the_losing_takes_and_the_manifest_names_only_files_on_disk()
    {
        using var shim = EnvShim(MetaKey);
        MetaFake meta = OverlongMeta(MeaningPasses.Three);
        // Window 4.0 (allowed 4.4): take 1 (5.0 s) fails timing, take 3 (4.05 s) wins, take 2 (4.3 s) is the runner-up.
        var tts = new FakeTts([5.0, 4.3, 4.05]);
        AuditionReport report = await BuildService(meta, tts).RunAsync(
            [new AuditionSegmentInput("seg-1", "A long line that runs over.", 4.0, "ashley")], "en", "es");
        string outputDir = Path.Combine(Path.GetTempPath(), "audition-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            string manifestPath = await AuditionOutputWriter.WriteAsync(report, outputDir);

            string auditionDir = Path.Combine(outputDir, "media", "audition");
            string[] onDisk = Directory.GetFiles(auditionDir).Select(Path.GetFileName).Order().ToArray()!;
            Assert.Equal(["seg-1-take1.wav", "seg-1-take2.wav", "seg-1-take3.wav"], onDisk);

            AuditionReport manifest = JsonSerializer.Deserialize<AuditionReport>(await File.ReadAllTextAsync(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            AuditionTakeReport[] takes = manifest.Segments[0].Takes;
            Assert.Equal(AuditionTimingVerdict.Fail, takes[0].TimingVerdict);
            Assert.True(takes[2].IsWinner);
            foreach (AuditionTakeReport take in takes)
            {
                string path = Path.Combine(auditionDir, take.AudioFileName!);
                Assert.True(File.Exists(path), $"Manifest names {take.AudioFileName} but it was not written.");
                Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant(), take.AudioSha256);
            }

            Assert.Equal("seg-1-take3.wav", manifest.Segments[0].WinnerFileName);
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Manifest_does_not_name_a_file_for_a_take_whose_audio_was_not_written()
    {
        var cast = new AuditionCastVoice("anna", "v", "Alice");
        AuditionTakeReport take = new(
            1, QaAttemptKind.Initial, "Hola.", MeaningVerdict.Pass, [], "ok", Synthesized: true, 1.0, 1.0,
            AuditionTimingVerdict.Pass, IsWinner: true, "seg-take1.wav", null, 1, 1, 5);
        var segment = new AuditionSegmentReport("seg", "Hello.", "anna", cast, 1.0, [take], true, 1, "seg-take1.wav");
        var report = new AuditionReport("en", "es", [cast], [segment], new AuditionTotals(1, 1, 1, 0, 0, 0, 0, false, false));
        string outputDir = Path.Combine(Path.GetTempPath(), "audition-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            string manifestPath = await AuditionOutputWriter.WriteAsync(report, outputDir);

            AuditionReport manifest = JsonSerializer.Deserialize<AuditionReport>(await File.ReadAllTextAsync(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Null(manifest.Segments[0].Takes[0].AudioFileName);
            Assert.Null(manifest.Segments[0].WinnerFileName);
            Assert.Empty(Directory.GetFiles(Path.Combine(outputDir, "media", "audition")));
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task NoWinning_take_reports_line_uncast_with_verdicts_preserved()
    {
        using var shim = EnvShim(MetaKey);
        MetaFake meta = OverlongMeta(MeaningPasses.Three);
        // Alternates and the speed variant are now attempted before a line stays
        // uncast: one overlong alternate, meaning-passing, then the speed variant.
        meta.VariantsJsons.Enqueue("""{"variants":["Frase larga variante que sigue y sigue y sigue y sigue y sigue y sigue y sigue y sigue."]}""");
        meta.MeaningJsons.Enqueue(PassJson);
        var tts = new FakeTts([6.0, 6.0, 6.0, 6.0, 6.0]);

        AuditionReport report = await BuildService(meta, tts).RunAsync(
            [new AuditionSegmentInput("seg-1", "A long line that runs over.", 4.0, "anna")], "en", "es");

        AuditionSegmentReport line = report.Segments[0];
        Assert.Equal(
            (1, false, (int?)null, (string?)null),
            (report.Segments.Length, line.Cast, line.WinnerTakeNumber, line.WinnerFileName));

        AuditionTotals totals = report.Totals;
        Assert.Equal(
            (0, 5, 5, QaAttemptKind.Alternate, QaAttemptKind.SpeedVariant, AuditionService.SpeedVariantFactor, 9),
            (totals.LinesCast, line.Takes.Length, totals.TakesSynthesized, line.Takes[3].Kind, line.Takes[4].Kind, line.Takes[4].SpeedFactor!.Value, totals.MetaCalls));

        Assert.All(line.Takes, t => Assert.Equal(
            (MeaningVerdict.Pass, true, AuditionTimingVerdict.Fail, false),
            (t.MeaningVerdict, t.Synthesized, t.TimingVerdict, t.IsWinner)));
    }

    [Fact]
    public async Task Character_cap_stops_synthesis_for_the_run()
    {
        using var shim = EnvShim(MetaKey);
        var meta = new MetaFake();
        var segments = new List<AuditionSegmentInput>();
        for (int i = 1; i <= 6; i++)
        {
            meta.Translations.Enqueue(new string((char)('a' + i), 600));
            meta.MeaningJsons.Enqueue(PassJson);
            segments.Add(new AuditionSegmentInput($"seg-{i}", $"Line number {i}.", 45.0, i % 2 == 1 ? "anna" : "ashley"));
        }
        var tts = new FakeTts([45.0, 45.0, 45.0, 45.0, 45.0, 45.0]);

        AuditionReport report = await BuildService(meta, tts).RunAsync(segments, "en", "es");

        // Five 600-char takes reach exactly 3000; the sixth would exceed it.
        AuditionTotals totals = report.Totals;
        Assert.Equal(
            (true, false, 3000, 5, 5, 5),
            (totals.ElevenLabsCapHit, totals.MetaCapHit, totals.ElevenLabsCharacters, totals.TakesSynthesized, tts.SynthCalls.Count, totals.LinesCast));

        AuditionSegmentReport last = report.Segments[^1];
        AuditionTakeReport lastTake = last.Takes[0];
        Assert.Equal((false, false), (lastTake.Synthesized, last.Cast));
        Assert.Contains("cap", lastTake.SynthesisNote ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingApiKey_fails_closed_with_zero_calls()
    {
        using var metaShim = EnvShim(MetaKey);
        using var elevenLabsShim = EnvShim(NoElevenLabsKey);
        var meta = new MetaFake();
        meta.Translations.Enqueue("Hola.");
        meta.MeaningJsons.Enqueue(PassJson);
        using var provider = new ElevenLabsTtsProvider(new HttpClient(new DelegatingHandlerStub((_, _) =>
            throw new InvalidOperationException("ElevenLabs HTTP must not be called without a key."))));
        var service = new AuditionService(meta.BuildClient(), provider, QaOptions.Default);

        await Assert.ThrowsAsync<TtsApiException>(() =>
            service.RunAsync([new AuditionSegmentInput("seg-1", "Hello.", 1.0, "anna")], "en", "es"));

        Assert.Equal(0, meta.Calls);
    }

    [Fact]
    public async Task FirstPass_line_failing_measured_timing_receives_alternates()
    {
        using var shim = EnvShim(MetaKey);
        var meta = new MetaFake();
        // QA passes on attempt 1 (estimate 13c/15 = 0.87s fits window 2.0+10%):
        // exactly one candidate, the byproduct problem from the Oct 30 audition.
        meta.Translations.Enqueue("Hola, Ashley.");
        meta.MeaningJsons.Enqueue(PassJson);
        meta.VariantsJsons.Enqueue("""{"variants":["Hola, Ashley, qué gusto.","Saludos, Ashley."]}""");
        meta.MeaningJsons.Enqueue(PassJson);
        meta.MeaningJsons.Enqueue(PassJson);
        // Measured: initial 3.0 fails (allowed 2.2), alt1 2.6 fails, alt2 2.1 passes.
        var tts = new FakeTts([3.0, 2.6, 2.1]);

        AuditionReport report = await BuildService(meta, tts).RunAsync(
            [new AuditionSegmentInput("seg-1", "Hi, Ashley.", 2.0, "anna")], "en", "es");

        AuditionSegmentReport line = report.Segments[0];
        Assert.Equal(
            (1, 3, QaAttemptKind.Initial, QaAttemptKind.Alternate, QaAttemptKind.Alternate, true, 3, "seg-1-take3.wav", "Saludos, Ashley."),
            (report.Segments.Length, line.Takes.Length, line.Takes[0].Kind, line.Takes[1].Kind, line.Takes[2].Kind, line.Cast, line.WinnerTakeNumber!.Value, line.WinnerFileName!, line.Takes[2].Text));
        Assert.All(line.Takes, t => Assert.Equal(
            (MeaningVerdict.Pass, true),
            (t.MeaningVerdict, t.Synthesized)));

        // 1 translation + 1 QA meaning + 1 variants + 2 alternate meaning checks.
        Assert.Equal(
            (5, 5, 1, 3, false, true),
            (report.Totals.MetaCalls, meta.Calls, meta.VariantsCalls, report.Totals.TakesSynthesized, line.Takes.Any(t => t.Kind == QaAttemptKind.SpeedVariant), tts.SynthSpeeds.All(s => s is null)));
    }

    [Fact]
    public async Task MeaningFailing_alternate_is_never_synthesized()
    {
        using var shim = EnvShim(MetaKey);
        var meta = new MetaFake();
        meta.Translations.Enqueue("Hola, Ashley.");
        meta.MeaningJsons.Enqueue(PassJson);
        meta.VariantsJsons.Enqueue("""{"variants":["Variante uno de Ashley.","Saludos, Ashley."]}""");
        meta.MeaningJsons.Enqueue(FailJson("dropped greeting"));
        meta.MeaningJsons.Enqueue(PassJson);
        var tts = new FakeTts([3.0, 2.1]);

        AuditionReport report = await BuildService(meta, tts).RunAsync(
            [new AuditionSegmentInput("seg-1", "Hi, Ashley.", 2.0, "anna")], "en", "es");

        AuditionSegmentReport line = report.Segments[0];
        Assert.Equal(
            (1, 3, QaAttemptKind.Alternate, MeaningVerdict.Fail, false),
            (report.Segments.Length, line.Takes.Length, line.Takes[1].Kind, line.Takes[1].MeaningVerdict, line.Takes[1].Synthesized));
        Assert.Contains("Meaning gate", line.Takes[1].SynthesisNote);

        // Only the initial candidate and the meaning-passing alternate reached TTS.
        Assert.Equal(
            (2, "Hola, Ashley.", "Saludos, Ashley.", 2, true, 3),
            (tts.SynthCalls.Count, tts.SynthCalls[0].Text, tts.SynthCalls[1].Text, report.Totals.TakesSynthesized, line.Cast, line.WinnerTakeNumber!.Value));
    }

    [Fact]
    public async Task Speed_variant_is_labeled_and_measured()
    {
        using var shim = EnvShim(MetaKey);
        var meta = new MetaFake();
        meta.Translations.Enqueue("Hola, Ashley.");
        meta.MeaningJsons.Enqueue(PassJson);
        meta.VariantsJsons.Enqueue("""{"variants":["Saludos, Ashley."]}""");
        meta.MeaningJsons.Enqueue(PassJson);
        // Initial 3.0 (err 0.8) and alternate 2.6 (err 0.4) both fail; the speed
        // variant re-synthesizes the closer text (the alternate) at 1.15 -> 2.1 passes.
        var tts = new FakeTts([3.0, 2.6, 2.1]);

        AuditionReport report = await BuildService(meta, tts).RunAsync(
            [new AuditionSegmentInput("seg-1", "Hi, Ashley.", 2.0, "anna")], "en", "es");

        AuditionSegmentReport line = report.Segments[0];
        AuditionTakeReport speed = line.Takes[2];
        Assert.Equal(
            (1, 3, QaAttemptKind.SpeedVariant, AuditionService.SpeedVariantFactor, "Saludos, Ashley.", MeaningVerdict.Pass, 0, true, 2.1, AuditionTimingVerdict.Pass, true, 3, "seg-1-take3.wav"),
            (report.Segments.Length, line.Takes.Length, speed.Kind, speed.SpeedFactor!.Value, speed.Text, speed.MeaningVerdict, speed.MeaningTokens, speed.Synthesized, speed.MeasuredSeconds!.Value, speed.TimingVerdict, speed.IsWinner, line.WinnerTakeNumber!.Value, line.WinnerFileName!));

        Assert.Equal([null, null, AuditionService.SpeedVariantFactor], tts.SynthSpeeds);
        // No Meta call for the speed variant: 1 translation + 1 QA meaning + 1 variants + 1 alternate meaning.
        Assert.Equal(4, report.Totals.MetaCalls);
    }

    [Fact]
    public async Task PerSegment_take_cap_stops_generation_before_the_speed_variant()
    {
        using var shim = EnvShim(MetaKey);
        MetaFake meta = OverlongMeta(MeaningPasses.Three);
        meta.VariantsJsons.Enqueue("""{"variants":["Frase larga variante uno que sigue y sigue y sigue y sigue y sigue y sigue y sigue.","Frase larga variante dos que sigue y sigue y sigue y sigue y sigue y sigue y sigue."]}""");
        meta.MeaningJsons.Enqueue(PassJson);
        meta.MeaningJsons.Enqueue(PassJson);
        var tts = new FakeTts([6.0, 6.0, 6.0, 6.0, 6.0]);

        AuditionReport report = await BuildService(meta, tts).RunAsync(
            [new AuditionSegmentInput("seg-1", "A long line that runs over.", 4.0, "anna")], "en", "es");

        AuditionSegmentReport line = report.Segments[0];
        // 3 candidates + 2 alternates synthesized = the all-in cap of 5; the speed
        // variant is listed but not synthesized, with the cap note preserved.
        Assert.Equal(
            (1, 6, 5, 5, 5, QaAttemptKind.SpeedVariant, false, false, (int?)null),
            (report.Segments.Length, line.Takes.Length, line.Takes.Count(t => t.Synthesized), report.Totals.TakesSynthesized, tts.SynthCalls.Count, line.Takes[5].Kind, line.Takes[5].Synthesized, line.Cast, line.WinnerTakeNumber));
        Assert.Contains("cap", line.Takes[5].SynthesisNote ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
