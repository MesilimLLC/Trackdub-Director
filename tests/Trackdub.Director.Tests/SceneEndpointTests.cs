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
/// Scene-context, director-adaptation and visual-localisation endpoint tests.
/// </summary>
public sealed class SceneEndpointTests
{
    private static string FrameSha256Hex()
    {
        byte[] bytes = Convert.FromBase64String("iVBORw0KGgo=");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static void AssertSceneVisionContext(JsonElement context)
    {
        Assert.Equal("A woman stands outside a coffee shop entrance talking on her cell phone while smiling.", context.GetProperty("scene_summary").GetString());
        Assert.Equal("cheerful", context.GetProperty("emotion_tone_label").GetString());
        Assert.Equal("a smiling woman in a knit beanie and black winter coat talking on a phone", context.GetProperty("speaker_description").GetString());
    }

    private static void AssertSceneVisionTextSplit(JsonElement context)
    {
        string[] onScreen = context.GetProperty("on_screen_text").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("COFFEE SHOP", onScreen);
        Assert.DoesNotContain("Okay. My apartment is across from a big department store.", onScreen);
        Assert.DoesNotContain("VOA Learning English", onScreen);
        string[] burned = context.GetProperty("burned_in_text").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["VOA Learning English", "Okay. My apartment is across from a big department store."], burned);
    }

    private static void AssertSceneVisionMeta(JsonElement seg)
    {
        JsonElement visionProvenance = seg.GetProperty("vision_provenance");
        Assert.Equal("muse-spark-1.1", visionProvenance.GetProperty("requested_model_id").GetString());
        Assert.Equal("muse-spark-1.1", visionProvenance.GetProperty("model_id").GetString());
        Assert.Equal("scene-vision-v2", visionProvenance.GetProperty("prompt_template_version").GetString());
        JsonElement visionUsage = seg.GetProperty("vision_usage");
        Assert.Equal(3634, visionUsage.GetProperty("total_tokens").GetInt32());
        Assert.Equal(611, visionUsage.GetProperty("reasoning_tokens").GetInt32());
    }

    private static void AssertSceneFrameProvenance(JsonElement seg)
    {
        Assert.Equal("hero-0242", seg.GetProperty("frame_id").GetString());
        Assert.Equal(162.0, seg.GetProperty("timecode_seconds").GetDouble());
        Assert.Equal(FrameSha256Hex(), seg.GetProperty("frame_sha256").GetString());
    }

    private static void AssertSceneVisionTotals(JsonElement root)
    {
        Assert.Equal(3634, root.GetProperty("vision_usage_total").GetProperty("total_tokens").GetInt32());
        Assert.Equal(611, root.GetProperty("vision_usage_total").GetProperty("reasoning_tokens").GetInt32());
    }

    private static void AssertSceneRoot(JsonElement root, out JsonElement seg)
    {
        Assert.Equal(1, root.GetProperty("segments").GetArrayLength());
        seg = root.GetProperty("segments")[0];
        Assert.Equal("seg-001", seg.GetProperty("id").GetString());
    }

    private static void AssertSceneStatus(JsonElement seg)
    {
        Assert.Equal("Parsed", seg.GetProperty("status").GetString());
        Assert.Equal("spk-1", seg.GetProperty("context").GetProperty("speaker_id").GetString());
    }

    private static void AssertSceneSpeakerNulls(JsonElement context)
    {
        Assert.False(context.TryGetProperty("speaker_label", out _));
        Assert.False(context.TryGetProperty("emotion_tone_label", out _));
    }

    private static void AssertSceneContentNulls(JsonElement context)
    {
        Assert.False(context.TryGetProperty("on_screen_text", out _));
        Assert.False(context.TryGetProperty("scene_summary", out _));
    }

    private static void AssertSceneRegion(JsonElement region)
    {
        Assert.Equal("red rectangle", region.GetProperty("concept").GetString());
        Assert.Equal(10, region.GetProperty("x1").GetInt32());
        Assert.Equal(29, region.GetProperty("x2").GetInt32());
    }

    private static void AssertSceneRegionWidth(JsonElement region)
    {
        Assert.Equal(64, region.GetProperty("frame_width").GetInt32());
    }

    private static void AssertSceneProvenance(JsonElement provenance)
    {
        Assert.Equal("sam-3.1", provenance.GetProperty("model_id").GetString());
        Assert.Equal("scene-v1", provenance.GetProperty("prompt_template_version").GetString());
    }

    private static void AssertSceneCounts(JsonElement root, JsonElement seg)
    {
        Assert.Equal(1, seg.GetProperty("images_processed").GetInt32());
        Assert.Equal(1, root.GetProperty("images_processed_total").GetInt32());
    }

    private static HttpMessageHandler SceneHandler()
    {
        return new DelegatingHandlerStub((request, _) =>
        {
            string? path = request.RequestUri?.AbsolutePath;
            Assert.True(path is not null && path.EndsWith("/responses", StringComparison.Ordinal), $"Expected /responses, got {path}");
            var payload = new
            {
                id = "resp_test",
                @object = "response",
                status = "completed",
                model = "sam-3.1",
                error = (object?)null,
                output = new[]
                {
                    new
                    {
                        type = "message",
                        role = "assistant",
                        status = "completed",
                        content = new[]
                        {
                            new
                            {
                                type = "output_text",
                                text = "<0f>0<|box;x1=10;y1=10;x2=29;y2=49;w=64;h=64|><|mask;x=0;y=0;data=40,20,abc|>",
                                annotations = Array.Empty<object>(),
                                logprobs = Array.Empty<object>(),
                            },
                        },
                    },
                },
                usage = new
                {
                    input_tokens = 0,
                    input_tokens_details = new { cached_tokens = 0 },
                    output_tokens = 0,
                    output_tokens_details = new { reasoning_tokens = 0 },
                    total_tokens = 0,
                    images_processed = 1,
                },
            };
            return Task.FromResult(JsonOk(payload));
        });
    }

    [Fact]
    public async Task SceneContext_happy_path_returns_parsed_region_and_honest_nulls()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(SceneHandler());
        using HttpClient client = factory.CreateClient();

        var body = new
        {
            segments = new[]
            {
                new
                {
                    id = "seg-001",
                    text = "We need one more take.",
                    frame_image_data_url = "data:image/png;base64,iVBORw0KGgo=",
                    concepts = new[] { "red rectangle" },
                    speaker_id = "spk-1",
                },
            },
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/scene-context", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        AssertSceneRoot(root, out JsonElement seg);
        AssertSceneStatus(seg);

        JsonElement context = seg.GetProperty("context");
        // Nulls are omitted by the server's WhenWritingNull convention: absence == unknown.
        AssertSceneSpeakerNulls(context);
        AssertSceneContentNulls(context);

        JsonElement region = context.GetProperty("regions")[0];
        AssertSceneRegion(region);
        AssertSceneRegionWidth(region);

        JsonElement provenance = seg.GetProperty("provenance");
        AssertSceneProvenance(provenance);
        AssertSceneCounts(root, seg);
    }

    private static HttpMessageHandler VisionSceneHandler()
    {
        return new DelegatingHandlerStub((request, _) =>
        {
            string? path = request.RequestUri?.AbsolutePath;
            Assert.True(path is not null && path.EndsWith("/chat/completions", StringComparison.Ordinal), $"Expected /chat/completions, got {path}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ProbeVisionResponseBody, Encoding.UTF8, "application/json"),
            });
        });
    }

    [Fact]
    public async Task SceneContext_vision_enrichment_returns_split_fields_and_frame_provenance()
    {
        using var auth = FakeAuth();
        using var visionModel = EnvShim("META_VISION_MODEL", "muse-spark-1.1");
        using var factory = new ServerFactory(VisionSceneHandler());
        using HttpClient client = factory.CreateClient();

        var body = new
        {
            segments = new[]
            {
                new
                {
                    id = "seg-001",
                    text = "Okay. My apartment is across from a big department store.",
                    frame_image_data_url = "data:image/png;base64,iVBORw0KGgo=",
                    speaker_id = "ashley",
                    frame_id = "hero-0242",
                    timecode_seconds = (double?)162.0,
                    frame_sha256 = FrameSha256Hex(),
                    vision_enrichment = (bool?)true,
                },
            },
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/scene-context", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        AssertSceneRoot(root, out JsonElement seg);
        Assert.Equal("Parsed", seg.GetProperty("status").GetString());
        JsonElement context = seg.GetProperty("context");
        Assert.Equal("ashley", context.GetProperty("speaker_id").GetString());
        AssertSceneVisionContext(context);
        AssertSceneVisionTextSplit(context);
        AssertSceneVisionMeta(seg);
        AssertSceneFrameProvenance(seg);
        AssertSceneVisionTotals(root);
        // The frame image itself is never stored or echoed back.
        Assert.DoesNotContain("iVBORw0KGgo", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectorAdaptation_passes_both_gates_and_reports_context_fields()
    {
        using var auth = FakeAuth();
        var handler = new DelegatingHandlerStub(async (request, cancellationToken) =>
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal)
                ? ChatOk(new ChatReply("muse-spark-1.1", """{"verdict":"pass","issues":[],"explanation":"ok"}""", 90, 30))
                : ChatOk(new ChatReply("muse-spark-1.1", "Perfecto, una toma más.", 120, 40));
        });
        using var factory = new ServerFactory(handler);
        using HttpClient client = factory.CreateClient();

        var requestBody = new
        {
            segments = new[]
            {
                new
                {
                    id = "seg-002",
                    text = "We need one more take.",
                    translation = "Necesitamos una toma más.",
                    duration_seconds = 4.0,
                    context = new { emotion_tone_label = "cheerful" },
                    director_adaptation = new { target_register = "informal" },
                },
            },
            sourceLanguage = "en",
            targetLanguage = "es",
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/director-adaptation", requestBody);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement seg = doc.RootElement.GetProperty("segments")[0];
        ExpectStrings(
            seg,
            ("status", "Passed"),
            ("target_register", "informal"),
            ("adapted_translation", "Perfecto, una toma más."),
            ("timing_verdict", "Pass"),
            ("meaning.verdict", "Pass"),
            ("context_fields_used.0", "emotion_tone_label"),
            ("provenance.prompt_template_version", "director-v1"));

        bool changed = seg.GetProperty("changed").GetBoolean();
        Assert.True(changed);
    }

    [Fact]
    public async Task DirectorAdaptation_rejects_duplicate_segment_ids()
    {
        using var auth = FakeAuth();
        int calls = 0;
        var handler = new DelegatingHandlerStub((request, cancellationToken) =>
        {
            calls++;
            return Task.FromResult(ChatOk(new ChatReply("muse-spark-1.1", "x", 1, 1)));
        });
        using var factory = new ServerFactory(handler);
        using HttpClient client = factory.CreateClient();

        var requestBody = new
        {
            segments = new[]
            {
                new { id = "seg-008", text = "a", translation = "b", director_adaptation = new { target_register = "formal" } },
                new { id = "seg-008", text = "c", translation = "d", director_adaptation = new { target_register = "informal" } },
            },
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/director-adaptation", requestBody);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Visual_localisation_accepts_limit_and_duplicate_ids_in_input_order()
    {
        using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(UpstreamError(HttpStatusCode.InternalServerError))));
        using HttpClient client = factory.CreateClient();
        string body = "{\"segments\":[" + string.Join(",", Enumerable.Repeat("{\"id\":\"same\"}", 100)) + "]}";

        (HttpStatusCode status, JsonElement root) = await PostRawAsync(client, "/api/visual-localisation", body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(100, root.GetProperty("segments").GetArrayLength());
        Assert.All(root.GetProperty("segments").EnumerateArray(), s => Assert.Equal("same", s.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task SceneContext_reports_frame_hash_mismatch_without_calling_the_model()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(UpstreamError(HttpStatusCode.InternalServerError))));
        using HttpClient client = factory.CreateClient();
        string body = "{\"segments\":[{\"id\":\"a\",\"frame_image_data_url\":\"data:image/png;base64,iVBORw0KGgo=\",\"frame_sha256\":\"" + new string('0', 64) + "\"}]}";

        HttpResponseMessage resp = await client.PostAsync("/api/scene-context", new StringContent(body, Encoding.UTF8, "application/json"));
        string json = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Frame SHA-256 mismatch", json);
    }

    [Fact]
    public async Task SceneContext_accepts_a_raw_segment_array_without_frames()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(UpstreamError(HttpStatusCode.InternalServerError))));
        using HttpClient client = factory.CreateClient();

        (HttpStatusCode status, _) = await PostRawAsync(client, "/api/scene-context", "[{\"id\":\"a\",\"text\":\"Hi\"}]");

        Assert.Equal(HttpStatusCode.OK, status);
    }
}
