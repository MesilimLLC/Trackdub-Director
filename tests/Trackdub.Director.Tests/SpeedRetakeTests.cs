using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class SpeedRetakeTests
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

    private static MetaClient BuildMeta()
    {
        var handler = new HandlerStub(async (request, cancellationToken) =>
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Chat(body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal) ? PassJson : "Hola, Ashley.");
        });
        return new MetaClient(new HttpClient(handler));
    }

    /// <summary>Deterministic speed model: realized duration = base / requested speed, times an optional jitter.</summary>
    private sealed class FakeSpeedTts(double baseSeconds, double jitter = 1.0, bool supportsSpeed = true) : ITtsProvider
    {
        public List<double?> Speeds { get; } = [];

        public int PcmSampleRate => 16000;

        public int PcmChannels => 1;

        public int PcmBitsPerSample => 16;

        public bool SupportsSpeedSetting(string modelId) => supportsSpeed;

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
            Speeds.Add(request.Speed);
            double seconds = request.Speed is null ? baseSeconds : (baseSeconds / request.Speed.Value) * jitter;
            int frames = (int)Math.Round(seconds * PcmSampleRate);
            byte[] wav = AudioDuration.ToWav(new byte[frames * 2], new PcmFormat { SampleRate = PcmSampleRate, Channels = 1, BitsPerSample = 16 });
            return Task.FromResult(new TtsSynthesisResult(wav, request.Text.Length));
        }
    }

    private static async Task<AuditionReport> RunAsync(FakeSpeedTts tts, SpeedRetakeOptions options, double window = 2.0, double tolerance = 0.10)
    {
        string? previous = Environment.GetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv);
        Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, "test-fake-key");
        try
        {
            var qaOptions = new QaOptions { ToleranceFraction = tolerance };
            var service = new AuditionService(BuildMeta(), tts, qaOptions, new AuditionRuntimeOptions(SpeedRetake: options));
            return await service.RunAsync([new AuditionSegmentInput("seg-1", "Hi Ashley!", window, "anna")], "en", "es");
        }
        finally
        {
            Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, previous);
        }
    }

    [Fact]
    public async Task Deterministic_retake_lands_at_computed_speed_and_is_labeled()
    {
        // 2.30 s measured against a 2.0 s window: f = 1.15, over the 10% gate.
        var tts = new FakeSpeedTts(baseSeconds: 2.30);
        AuditionReport report = await RunAsync(tts, new SpeedRetakeOptions(Enabled: true, Cap: 1.20));

        AuditionSegmentReport line = Assert.Single(report.Segments);
        Assert.True(line.Cast);
        AuditionTakeReport retake = line.Takes.Single(t => t.Kind == QaAttemptKind.SpeedRetake);
        Assert.Equal(1.15, retake.ComputedFactor!.Value, 3);
        Assert.Equal(1.15, retake.SpeedRequested, 3);
        Assert.False(retake.CalibrationCorrected);
        Assert.Equal(AuditionTimingVerdict.Pass, retake.TimingVerdict);
        Assert.Equal(line.WinnerTakeNumber, retake.TakeNumber);

        SpeedCalibrationSample sample = Assert.Single(report.SpeedCalibration!);
        Assert.Equal(1.15, sample.RequestedSpeed, 3);
        Assert.Equal(2.0, sample.RealizedDuration, 2);
        Assert.Equal(2.30 / 2.0, sample.PredictedDuration * sample.RequestedSpeed / sample.RealizedDuration, 2);
    }

    [Fact]
    public async Task Cap_boundary_attempts_at_factor_equal_to_cap_and_not_above()
    {
        // f = 2.42 / 2.2 = 1.10 exactly at the default cap: retake attempted
        // (zero tolerance here so the measured take fails the timing gate at all).
        var atCap = new FakeSpeedTts(baseSeconds: 2.42);
        AuditionReport atCapReport = await RunAsync(atCap, new SpeedRetakeOptions(Enabled: true), window: 2.2, tolerance: 0);
        Assert.Contains(atCapReport.Segments[0].Takes, t => t.Kind == QaAttemptKind.SpeedRetake);

        // The on-record exhibit shape: 2.46 / 2.2 = 1.118 over the default cap: no retake.
        var overCap = new FakeSpeedTts(baseSeconds: 2.46);
        AuditionReport overReport = await RunAsync(overCap, new SpeedRetakeOptions(Enabled: true), window: 2.2);
        Assert.DoesNotContain(overReport.Segments[0].Takes, t => t.Kind == QaAttemptKind.SpeedRetake);
        Assert.DoesNotContain(overCap.Speeds, s => s is not null);
    }

    [Fact]
    public async Task Noisy_model_is_decided_by_remeasurement_and_gets_one_retake_only()
    {
        // Retake realized 8% slow: predicted inside, measured still outside.
        var tts = new FakeSpeedTts(baseSeconds: 2.30, jitter: 1.15);
        AuditionReport report = await RunAsync(tts, new SpeedRetakeOptions(Enabled: true, Cap: 1.20));

        AuditionSegmentReport line = Assert.Single(report.Segments);
        Assert.Single(line.Takes, t => t.Kind == QaAttemptKind.SpeedRetake);
        AuditionTakeReport retake = line.Takes.Single(t => t.Kind == QaAttemptKind.SpeedRetake);
        Assert.Equal(AuditionTimingVerdict.Fail, retake.TimingVerdict);
        Assert.False(line.Cast && line.WinnerTakeNumber == retake.TakeNumber);
    }

    [Fact]
    public async Task Unsupported_model_retake_omits_speed_and_logs_unsupported()
    {
        var tts = new FakeSpeedTts(baseSeconds: 2.30, supportsSpeed: false);
        AuditionReport report = await RunAsync(tts, new SpeedRetakeOptions(Enabled: true, Cap: 1.20));

        AuditionTakeReport retake = Assert.Single(report.Segments[0].Takes, t => t.Kind == QaAttemptKind.SpeedRetake);
        Assert.Equal(1.0, retake.SpeedRequested);
        Assert.Contains("unsupported", retake.SynthesisNote, StringComparison.OrdinalIgnoreCase);
        Assert.All(tts.Speeds, s => Assert.Null(s));
        Assert.Empty(report.SpeedCalibration!);
    }

    [Fact]
    public void Calibration_math_median_clamp_and_passthrough()
    {
        static SpeedCalibrationSample Sample(double requested, double ratio) => new("v1", "m1", requested, 2.0, 2.0 * ratio, ratio);

        // Fewer than 3 samples: passthrough, no correction.
        Assert.Equal(1.0, SpeedRetakeMath.CorrectionFactor([Sample(1.1, 0.90), Sample(1.1, 0.90)], "v1", "m1"));

        // Median response 0.98/1.0 pattern: correction divides the factor.
        var samples = new[] { Sample(1.10, 1.078), Sample(1.10, 1.078), Sample(1.20, 1.176) };
        double requested = SpeedRetakeMath.RequestedSpeed(new SpeedRetakeRequest(2.30, 2.0, 1.20, samples, "v1", "m1"), out double factor, out bool corrected);
        Assert.Equal(1.15, factor, 3);
        Assert.True(corrected);
        Assert.Equal(Math.Round(1.15 / 0.98, 3), requested, 3);

        // Wild median clamps to the 1.05 correction bound; speed clamps to cap and never below 1.0.
        var wild = new[] { Sample(1.1, 1.40), Sample(1.1, 1.40), Sample(1.1, 1.40) };
        Assert.Equal(1.05, SpeedRetakeMath.CorrectionFactor(wild, "v1", "m1"), 3);
        double clamped = SpeedRetakeMath.RequestedSpeed(new SpeedRetakeRequest(2.60, 2.0, 1.10, [], "v1", "m1"), out _, out _);
        Assert.Equal(1.10, clamped, 3);
    }

    [Fact]
    public async Task Manifest_carries_new_fields_and_calibration_sidecar_appends()
    {
        var tts = new FakeSpeedTts(baseSeconds: 2.30);
        AuditionReport report = await RunAsync(tts, new SpeedRetakeOptions(Enabled: true, Cap: 1.20));
        string dir = Path.Combine(Path.GetTempPath(), "speed-retake-" + Guid.NewGuid().ToString("N"));
        try
        {
            await AuditionOutputWriter.WriteAsync(report, dir);
            string manifest = await File.ReadAllTextAsync(Path.Combine(dir, AuditionOutputWriter.ManifestFileName));
            Assert.Contains("\"speed_requested\"", manifest, StringComparison.Ordinal);
            Assert.Contains("\"computed_factor\"", manifest, StringComparison.Ordinal);
            Assert.Contains("\"calibration_corrected\"", manifest, StringComparison.Ordinal);

            string sidecarPath = Path.Combine(dir, AuditionOutputWriter.SpeedCalibrationFileName);
            Assert.True(File.Exists(sidecarPath));
            await AuditionOutputWriter.WriteAsync(report, dir);
            SpeedCalibrationSample[] all = JsonSerializer.Deserialize<SpeedCalibrationSample[]>(
                await File.ReadAllTextAsync(sidecarPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(2, all.Length);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Config_off_matches_default_service_behavior()
    {
        var first = new FakeSpeedTts(baseSeconds: 2.30);
        AuditionReport disabled = await RunAsync(first, SpeedRetakeOptions.Disabled);

        var second = new FakeSpeedTts(baseSeconds: 2.30);
        string? previous = Environment.GetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv);
        Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, "test-fake-key");
        AuditionReport baseline;
        try
        {
            baseline = await new AuditionService(BuildMeta(), second, QaOptions.Default)
                .RunAsync([new AuditionSegmentInput("seg-1", "Hi Ashley!", 2.0, "anna")], "en", "es");
        }
        finally
        {
            Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, previous);
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(JsonSerializer.Serialize(baseline, options), JsonSerializer.Serialize(disabled, options));
        Assert.DoesNotContain(disabled.Segments[0].Takes, t => t.Kind == QaAttemptKind.SpeedRetake);
    }
}
