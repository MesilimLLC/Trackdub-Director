using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class PipelineServiceTests
{
    private const string PassJson = """{"verdict":"pass","issues":[],"explanation":"ok"}""";

    private const string CannedTranscribeBody = """{"sessionId":"voyager/transcribe/PIPE","transcript":"Hi, Ashley.","audioDurationMs":1260,"turns":[{"turnId":0,"startMs":60,"endMs":1260,"transcript":"Hi, Ashley.","speaker":"A"}]}""";

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private sealed class FakeTranscribeClient(TranscribeRawResponse? response, Exception? failure = null) : IMuseTranscribeClient
    {
        public int Calls;

        public Task<TranscribeRawResponse> TranscribeAsync(Stream wavAudio, string? fileName = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (failure is not null)
            {
                throw failure;
            }

            return Task.FromResult(response!);
        }
    }

    private sealed class FakeTts : ITtsProvider
    {
        public int SynthCalls;

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
            SynthCalls++;
            byte[] pcm = new byte[16000 * 2];
            byte[] wav = AudioDuration.ToWav(pcm, new PcmFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 });
            return Task.FromResult(new TtsSynthesisResult(wav, text.Length));
        }
    }

    /// <summary>Meta handler that records call order and serves chat + scene responses from queues.</summary>
    private sealed class MetaFake
    {
        public readonly Queue<string> Translations = new();
        public readonly Queue<string> MeaningJsons = new();
        public readonly List<string> CallOrder = [];
        public bool FailChat;

        public HttpMessageHandler BuildHandler()
        {
            return new DelegatingHandlerStub(async (request, cancellationToken) =>
            {
                string? path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/responses", StringComparison.Ordinal))
                {
                    CallOrder.Add("scene");
                    return JsonOk(new
                    {
                        id = "resp_test",
                        status = "completed",
                        model = "sam-3.1",
                        output = new[]
                        {
                            new
                            {
                                type = "message",
                                role = "assistant",
                                status = "completed",
                                content = new[] { new { type = "output_text", text = string.Empty } },
                            },
                        },
                        usage = new { input_tokens = 0, output_tokens = 0, total_tokens = 0, images_processed = 1 },
                    });
                }

                if (FailChat)
                {
                    CallOrder.Add("chat:500");
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("{\"error\":\"upstream failure\"}", Encoding.UTF8, "application/json"),
                    };
                }

                string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal))
                {
                    CallOrder.Add("meaning");
                    return ChatOk(MeaningJsons.Count > 0 ? MeaningJsons.Dequeue() : PassJson, 7, 4);
                }

                CallOrder.Add("translate");
                return ChatOk(Translations.Count > 0 ? Translations.Dequeue() : "Hola.", 10, 5);
            });
        }

        private static HttpResponseMessage JsonOk(object payload)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
        }

        private static HttpResponseMessage ChatOk(string content, int promptTokens, int completionTokens)
        {
            return JsonOk(new
            {
                id = $"chatcmpl-test-{Guid.NewGuid():N}",
                @object = "chat.completion",
                created = 1234567890L,
                model = "muse-spark-1.1",
                choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
                usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens, total_tokens = promptTokens + completionTokens },
            });
        }
    }

    private static TranscribeRawResponse CannedRaw()
    {
        return JsonSerializer.Deserialize<TranscribeRawResponse>(CannedTranscribeBody, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static IDisposable EnvShim(string key, string? value)
    {
        string? prev = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
        return new Restore(key, prev);
    }

    private sealed class Restore(string key, string? prev) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(key, prev);
    }

    private static IDisposable FakeAuth()
    {
        var a = EnvShim(MetaAuth.PrimaryApiKeyEnv, "test-fake-key");
        var b = EnvShim(MetaAuth.ModelEnv, "muse-spark-1.1");
        var c = EnvShim(MetaAuth.BaseUrlEnv, "https://api.meta.ai/v1");
        var d = EnvShim(MetaAuth.ReasoningEffortEnv, "low");
        return new Composite(a, b, c, d);
    }

    private sealed class Composite(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable d in items)
            {
                d.Dispose();
            }
        }
    }

    private static PipelineService BuildService(MetaFake meta, IMuseTranscribeClient transcribe, ITtsProvider? tts)
    {
        return new PipelineService(new MetaClient(new HttpClient(meta.BuildHandler())), transcribe, tts, QaOptions.Default);
    }

    private static PipelineStageReport Stage(PipelineReport report, string stage)
    {
        return report.Stages.Single(s => s.Stage == stage);
    }

    [Fact]
    public async Task Full_run_executes_stages_in_order_and_keeps_lane_report_shapes()
    {
        using var auth = FakeAuth();
        using var elevenLabs = EnvShim(ElevenLabsAuth.ApiKeyEnv, "test-el-key");
        var meta = new MetaFake();
        meta.Translations.Enqueue("Hola, Ashley."); // pipeline QA
        meta.Translations.Enqueue("Hola, Ashley."); // audition's internal QA
        var transcribe = new FakeTranscribeClient(CannedRaw());
        var tts = new FakeTts();
        PipelineService service = BuildService(meta, transcribe, tts);

        var request = new PipelineRequest(
            [82, 73, 70, 70],
            Segments: null,
            SceneSegments: [new SceneSegmentInput("seg-001", "Hi, Ashley.", "data:image/png;base64,iVBORw0KGgo=", ["sign"])],
            Scene: true,
            Audition: true,
            AudioFileName: "clip.wav");
        PipelineReport report = await service.RunAsync(request);

        Assert.Equal(PipelineStageStatus.Ran, Stage(report, "transcribe").Status);
        Assert.Equal(PipelineStageStatus.Ran, Stage(report, "qa").Status);
        Assert.Equal(PipelineStageStatus.Ran, Stage(report, "scene").Status);
        Assert.Equal(PipelineStageStatus.Ran, Stage(report, "audition").Status);
        Assert.NotNull(report.Transcribe);
        Assert.NotNull(report.Qa);
        Assert.NotNull(report.Scene);
        Assert.NotNull(report.Audition);
        Assert.Equal("seg-001", report.Segments[0].Id);
        Assert.Equal("A", report.Segments[0].Speaker);

        // Lane order: translate+meaning (QA), scene, then the audition lane's own translate+meaning.
        Assert.Equal(["translate", "meaning", "scene", "translate", "meaning"], meta.CallOrder);
    }

    [Fact]
    public async Task Audition_is_off_by_default_and_recorded_as_skipped()
    {
        using var auth = FakeAuth();
        var meta = new MetaFake();
        var tts = new FakeTts();
        PipelineService service = BuildService(meta, new FakeTranscribeClient(CannedRaw()), tts);

        var request = new PipelineRequest(
            AudioWav: null,
            Segments: [new TranscribeSegment("seg-001", "Hi, Ashley.", 1.2, "A")]);
        PipelineReport report = await service.RunAsync(request);

        Assert.Equal(PipelineStageStatus.NotRun, Stage(report, "transcribe").Status);
        Assert.Equal(PipelineStageStatus.Ran, Stage(report, "qa").Status);
        Assert.Equal(PipelineStageStatus.SkippedByFlag, Stage(report, "audition").Status);
        Assert.Null(report.Audition);
        Assert.Equal(0, tts.SynthCalls);
    }

    [Fact]
    public async Task Audition_flagged_without_elevenlabs_key_is_skipped_not_run()
    {
        using var auth = FakeAuth();
        using var elevenLabs = EnvShim(ElevenLabsAuth.ApiKeyEnv, "");
        var meta = new MetaFake();
        var tts = new FakeTts();
        PipelineService service = BuildService(meta, new FakeTranscribeClient(CannedRaw()), tts);

        var request = new PipelineRequest(
            AudioWav: null,
            Segments: [new TranscribeSegment("seg-001", "Hi, Ashley.", 1.2, "A")],
            Audition: true);
        PipelineReport report = await service.RunAsync(request);

        PipelineStageReport stage = Stage(report, "audition");
        Assert.Equal(PipelineStageStatus.SkippedByFlag, stage.Status);
        Assert.Contains("ELEVENLABS_API_KEY", stage.Note, StringComparison.Ordinal);
        Assert.Null(report.Audition);
        Assert.Equal(0, tts.SynthCalls);
        // Only the QA lane's calls happened; the audition lane never ran.
        Assert.Equal(["translate", "meaning"], meta.CallOrder);
    }

    [Fact]
    public async Task Scene_flag_off_is_recorded_as_skipped_and_makes_no_scene_calls()
    {
        using var auth = FakeAuth();
        var meta = new MetaFake();
        PipelineService service = BuildService(meta, new FakeTranscribeClient(CannedRaw()), null);

        var request = new PipelineRequest(
            AudioWav: null,
            Segments: [new TranscribeSegment("seg-001", "Hi, Ashley.", 1.2, "A")],
            SceneSegments: [new SceneSegmentInput("seg-001", "Hi, Ashley.", "data:image/png;base64,iVBORw0KGgo=", ["sign"])],
            Scene: false);
        PipelineReport report = await service.RunAsync(request);

        Assert.Equal(PipelineStageStatus.SkippedByFlag, Stage(report, "scene").Status);
        Assert.Null(report.Scene);
        Assert.DoesNotContain("scene", meta.CallOrder);
    }

    [Fact]
    public async Task Scene_without_frames_runs_with_a_note_and_no_scene_calls()
    {
        using var auth = FakeAuth();
        var meta = new MetaFake();
        PipelineService service = BuildService(meta, new FakeTranscribeClient(CannedRaw()), null);

        var request = new PipelineRequest(
            AudioWav: null,
            Segments: [new TranscribeSegment("seg-001", "Hi, Ashley.", 1.2, "A")],
            SceneSegments: [new SceneSegmentInput("seg-001", "Hi, Ashley.")]);
        PipelineReport report = await service.RunAsync(request);

        PipelineStageReport stage = Stage(report, "scene");
        Assert.Equal(PipelineStageStatus.Ran, stage.Status);
        Assert.Equal("no segments with frames", stage.Note);
        Assert.DoesNotContain("scene", meta.CallOrder);
    }

    [Fact]
    public async Task Failed_qa_stage_stops_the_run_and_downstream_stages_never_run()
    {
        using var auth = FakeAuth();
        using var elevenLabs = EnvShim(ElevenLabsAuth.ApiKeyEnv, "test-el-key");
        var meta = new MetaFake { FailChat = true };
        var tts = new FakeTts();
        PipelineService service = BuildService(meta, new FakeTranscribeClient(CannedRaw()), tts);

        var request = new PipelineRequest(
            AudioWav: null,
            Segments: [new TranscribeSegment("seg-001", "Hi, Ashley.", 1.2, "A")],
            SceneSegments: [new SceneSegmentInput("seg-001", "Hi, Ashley.", "data:image/png;base64,iVBORw0KGgo=", ["sign"])],
            Scene: true,
            Audition: true);
        PipelineReport report = await service.RunAsync(request);

        Assert.Equal(PipelineStageStatus.Failed, Stage(report, "qa").Status);
        Assert.Equal(PipelineStageStatus.NotRun, Stage(report, "scene").Status);
        Assert.Equal(PipelineStageStatus.NotRun, Stage(report, "audition").Status);
        Assert.Null(report.Scene);
        Assert.Null(report.Audition);
        Assert.Equal(0, tts.SynthCalls);
        Assert.All(meta.CallOrder, c => Assert.StartsWith("chat:", c, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_transcribe_stage_stops_the_run_before_any_meta_call()
    {
        using var auth = FakeAuth();
        var meta = new MetaFake();
        var transcribe = new FakeTranscribeClient(null, new MetaApiException("Meta Model API HTTP 400: bad audio"));
        PipelineService service = BuildService(meta, transcribe, null);

        var request = new PipelineRequest([1, 2, 3], Segments: null, Audition: false);
        PipelineReport report = await service.RunAsync(request);

        Assert.Equal(PipelineStageStatus.Failed, Stage(report, "transcribe").Status);
        Assert.Equal(PipelineStageStatus.NotRun, Stage(report, "qa").Status);
        Assert.Equal(PipelineStageStatus.NotRun, Stage(report, "scene").Status);
        Assert.Equal(PipelineStageStatus.NotRun, Stage(report, "audition").Status);
        Assert.Empty(meta.CallOrder);
    }

    [Fact]
    public async Task Request_without_audio_or_segments_is_rejected()
    {
        using var auth = FakeAuth();
        var meta = new MetaFake();
        PipelineService service = BuildService(meta, new FakeTranscribeClient(CannedRaw()), null);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RunAsync(new PipelineRequest(null, null)));
        Assert.Empty(meta.CallOrder);
    }
}
