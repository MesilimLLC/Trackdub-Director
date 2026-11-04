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
/// Pipeline endpoint tests (stage ordering, skips and failure propagation).
/// </summary>
public sealed class PipelineEndpointTests
{
    private static HttpMessageHandler PipelineHandler(Queue<string> translations, Queue<string> meaningJsons, List<string> callOrder)
    {
        return new DelegatingHandlerStub(async (request, cancellationToken) =>
        {
            string? path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/asr/transcribe", StringComparison.Ordinal))
            {
                callOrder.Add("transcribe");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CannedTranscribeBody, Encoding.UTF8, "application/json"),
                };
            }

            if (path.EndsWith("/responses", StringComparison.Ordinal))
            {
                callOrder.Add("scene");
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

            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal))
            {
                callOrder.Add("meaning");
                string mj = meaningJsons.Count > 0 ? meaningJsons.Dequeue() : """{"verdict":"pass","issues":[],"explanation":"ok"}""";
                return ChatOk(new ChatReply("muse-spark-1.1", mj, 7, 4));
            }

            callOrder.Add("translate");
            return ChatOk(new ChatReply("muse-spark-1.1", translations.Count > 0 ? translations.Dequeue() : "Hola."));
        });
    }

    private static void ExpectStages(JsonElement root, params (string Stage, string Status)[] expected)
    {
        foreach ((string stage, string status) in expected)
        {
            Assert.Equal(status, StageStatus(root, stage));
        }
    }

    private static string StageStatus(JsonElement root, string stage)
    {
        return root.GetProperty("stages").EnumerateArray().Single(s => s.GetProperty("stage").GetString() == stage).GetProperty("status").GetString()!;
    }

    [Fact]
    public async Task Pipeline_happy_path_runs_lanes_in_order_with_audition_skipped_by_default()
    {
        using var auth = FakeAuth();
        var callOrder = new List<string>();
        using var factory = new ServerFactory(PipelineHandler(new Queue<string>(), new Queue<string>(), callOrder));
        using HttpClient client = factory.CreateClient();

        var body = new
        {
            segments = new[] { new { id = "seg-001", text = "Hi, Ashley.", duration_seconds = 1.2, speaker = "A" } },
            scene_segments = new[]
            {
                new { id = "seg-001", text = "Hi, Ashley.", frame_image_data_url = "data:image/png;base64,iVBORw0KGgo=", concepts = new[] { "sign" } },
            },
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/pipeline", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        ExpectStages(root, ("transcribe", "NotRun"), ("qa", "Ran"), ("scene", "Ran"), ("audition", "SkippedByFlag"));

        bool hasQa = root.TryGetProperty("qa", out _);
        bool hasScene = root.TryGetProperty("scene", out _);
        Assert.True(hasQa);
        Assert.True(hasScene);
        Assert.Equal(["translate", "meaning", "scene"], callOrder);
    }

    [Fact]
    public async Task Pipeline_transcribes_audio_first_when_supplied()
    {
        using var auth = FakeAuth();
        var callOrder = new List<string>();
        using var factory = new ServerFactory(PipelineHandler(new Queue<string>(), new Queue<string>(), callOrder));
        using HttpClient client = factory.CreateClient();

        var body = new
        {
            audio_base64 = Convert.ToBase64String([82, 73, 70, 70, 1, 0, 0, 0]),
            audio_file_name = "clip.wav",
            scene = false,
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/pipeline", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        ExpectStages(root, ("transcribe", "Ran"), ("qa", "Ran"), ("scene", "SkippedByFlag"), ("audition", "SkippedByFlag"));

        int segmentCount = root.GetProperty("segments").GetArrayLength();
        Assert.Equal(2, segmentCount);
        Assert.Equal(["transcribe", "translate", "meaning", "translate", "meaning"], callOrder);
    }

    [Fact]
    public async Task Pipeline_audition_flagged_without_elevenlabs_key_is_recorded_as_skipped()
    {
        using var auth = FakeAuth();
        using var elevenLabs = EnvShim(ElevenLabsAuth.ApiKeyEnv, "");
        var callOrder = new List<string>();
        using var factory = new ServerFactory(PipelineHandler(new Queue<string>(), new Queue<string>(), callOrder));
        using HttpClient client = factory.CreateClient();

        var body = new
        {
            segments = new[] { new { id = "seg-001", text = "Hi, Ashley.", duration_seconds = 1.2, speaker = "A" } },
            scene = false,
            audition = true,
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/pipeline", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("SkippedByFlag", StageStatus(root, "audition"));
        Assert.DoesNotContain("test-fake-key", json.ToLowerInvariant());
        Assert.Equal(["translate", "meaning"], callOrder);
    }

    [Fact]
    public async Task Pipeline_failed_stage_records_downstream_as_not_run()
    {
        using var auth = FakeAuth();
        var callOrder = new List<string>();
        var failing = new DelegatingHandlerStub((_, _) => Task.FromResult(UpstreamError(HttpStatusCode.InternalServerError)));
        using var factory = new ServerFactory(failing);
        using HttpClient client = factory.CreateClient();

        var body = new
        {
            segments = new[] { new { id = "seg-001", text = "Hi, Ashley.", duration_seconds = 1.2, speaker = "A" } },
            audition = true,
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/pipeline", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("Failed", StageStatus(root, "qa"));
        Assert.Equal("NotRun", StageStatus(root, "scene"));
        Assert.Equal("NotRun", StageStatus(root, "audition"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"segments":[]}""")]
    [InlineData("""{"audio_base64":"not base64!"}""")]
    public async Task Pipeline_rejects_requests_without_audio_or_segments(string body)
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(UpstreamError(HttpStatusCode.InternalServerError))));
        using HttpClient client = factory.CreateClient();

        (HttpStatusCode status, _) = await PostRawAsync(client, "/api/pipeline", body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }
}
