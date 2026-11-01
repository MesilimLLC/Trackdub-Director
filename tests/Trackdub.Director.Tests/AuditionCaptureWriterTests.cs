using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Regression: every synthesized take (winner and losers) is written, and
/// manifest filenames match files on disk.
/// </summary>
public sealed class AuditionCaptureWriterTests
{
    private const string PassJson = """{"verdict":"pass","issues":[],"explanation":"Meaning preserved."}""";

    [Fact]
    public async Task Writer_keeps_losing_and_winning_takes_on_disk_matching_manifest_names()
    {
        string? previousKey = Environment.GetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv);
        Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, "LLM_test-fake-key");
        try
        {
            // Reuse the alternates path: take1 timing-fails, take3 wins.
            var meta = new AuditionMetaFake();
            meta.Translations.Enqueue("Hola, Ashley.");
            meta.MeaningJsons.Enqueue(PassJson);
            meta.VariantsJsons.Enqueue("""{"variants":["Hola, Ashley, qué gusto.","Saludos, Ashley."]}""");
            meta.MeaningJsons.Enqueue(PassJson);
            meta.MeaningJsons.Enqueue(PassJson);
            var tts = new AuditionFakeTts([3.0, 2.6, 2.1]);

            AuditionReport report = await new AuditionService(meta.BuildClient(), tts, QaOptions.Default)
                .RunAsync([new AuditionSegmentInput("seg-1", "Hi, Ashley.", 2.0, "anna")], "en", "es");

            AuditionSegmentReport line = Assert.Single(report.Segments);
            Assert.Equal(3, line.Takes.Count(t => t.Synthesized));
            Assert.Contains(line.Takes, t => t.Synthesized && !t.IsWinner && t.TimingVerdict == AuditionTimingVerdict.Fail);
            Assert.Contains(line.Takes, t => t.IsWinner);

            string outputDir = Path.Combine(Path.GetTempPath(), "audition-capture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputDir);
            int written = await AuditionCaptureWriter.WriteAsync(outputDir, report);
            Assert.Equal(3, written);

            string auditionDir = Path.Combine(outputDir, "media", "audition");
            string[] filesOnDisk = Directory.GetFiles(auditionDir, "*.wav")
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            AuditionReport reloaded = JsonSerializer.Deserialize<AuditionReport>(
                await File.ReadAllTextAsync(Path.Combine(outputDir, "audition-manifest.json")),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

            string[] named = reloaded.Segments[0].Takes
                .Where(t => !string.IsNullOrWhiteSpace(t.AudioFileName))
                .Select(t => t.AudioFileName!)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(filesOnDisk, named);
            Assert.Contains("seg-1-take1.wav", filesOnDisk);
            Assert.Contains(line.WinnerFileName!, filesOnDisk);
            Assert.All(named, name => Assert.True(File.Exists(Path.Combine(auditionDir, name))));
        }
        finally
        {
            Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, previousKey);
        }
    }

    // Minimal copies of the audition test doubles (same contracts, local to this file).

    private sealed class AuditionFakeTts(IEnumerable<double> durations) : ITtsProvider
    {
        private readonly Queue<double> _durations = new(durations);

        public int PcmSampleRate => 16000;

        public int PcmChannels => 1;

        public int PcmBitsPerSample => 16;

        private PcmFormat Format => new()
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
            return Task.FromResult<IReadOnlyList<TtsVoice>>(
            [
                new TtsVoice("voice-alba", "Alba", "premade", "female", ["eleven_multilingual_v2"]),
                new TtsVoice("voice-zoe", "Zoe", "premade", "female", ["eleven_multilingual_v2"]),
            ]);
        }

        public Task<TtsSynthesisResult> SynthesizeAsync(string voiceId, string text, string languageCode, double? speed = null, CancellationToken cancellationToken = default)
        {
            double seconds = _durations.Count > 0 ? _durations.Dequeue() : 1.0;
            int frames = (int)Math.Round(seconds * PcmSampleRate);
            byte[] pcm = new byte[frames * 2];
            byte[] wav = AudioDuration.ToWav(pcm, Format);
            return Task.FromResult(new TtsSynthesisResult(wav, text.Length));
        }
    }

    private sealed class AuditionMetaFake
    {
        public Queue<string> Translations { get; } = new();

        public Queue<string> VariantsJsons { get; } = new();

        public Queue<string> MeaningJsons { get; } = new();

        public MetaClient BuildClient()
        {
            var handler = new DelegatingHandlerStub(async (request, cancellationToken) =>
            {
                string body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                string content;
                if (body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal))
                {
                    content = MeaningJsons.Dequeue();
                }
                else if (body.Contains("Alternates wanted", StringComparison.Ordinal))
                {
                    content = VariantsJsons.Dequeue();
                }
                else
                {
                    content = Translations.Dequeue();
                }

                var payload = new
                {
                    id = "chatcmpl-test",
                    @object = "chat.completion",
                    created = 1L,
                    model = "muse-spark-1.1",
                    choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 },
                };
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"),
                };
            });
            return new MetaClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) });
        }
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
