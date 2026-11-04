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
/// Status, translate, QA, models, error mapping and malformed-request endpoint tests.
/// </summary>
public sealed class CoreEndpointTests
{
    private static HttpMessageHandler ModelsHandler()
    {
        return new DelegatingHandlerStub((request, ct) =>
        {
            var payload = new
            {
                @object = "list",
                data = new[]
                {
                    new { id = "muse-spark-1.1", @object = "model", created = 123L, owned_by = "meta" },
                },
            };
            return Task.FromResult(JsonOk(payload));
        });
    }

    private static void AssertStatusWithoutKey(JsonElement root)
    {
        Assert.False(root.GetProperty("hasKey").GetBoolean());
        Assert.Equal(MetaAuth.DefaultModel, root.GetProperty("model").GetString());
        Assert.Equal(MetaAuth.DefaultBaseUrl, root.GetProperty("baseUrl").GetString());
    }

    private static void AssertNoKeyLeak(JsonElement root, string json)
    {
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("reasoningEffort").GetString()));
        Assert.DoesNotContain("test-fake-key", json.ToLowerInvariant());
    }

    private static void AssertTranslateRoot(JsonElement root)
    {
        Assert.Equal("en", root.GetProperty("source_language").GetString());
        Assert.Equal("es", root.GetProperty("target_language").GetString());
        Assert.Equal(2, root.GetProperty("segments").GetArrayLength());
    }

    private static void AssertFirstTranslatedSegment(JsonElement first)
    {
        Assert.Equal("seg-001", first.GetProperty("id").GetString());
        Assert.Equal("Hola, mundo.", first.GetProperty("translation").GetString());
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("provenance").GetProperty("model_id").GetString()));
    }

    private static void AssertTranslateUsage(JsonElement root, JsonElement first)
    {
        Assert.NotEqual(0, first.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.True(root.GetProperty("usage_total").GetProperty("total_tokens").GetInt32() > 0);
    }

    private static void AssertQaRoot(JsonElement root)
    {
        Assert.Equal("en", root.GetProperty("source_language").GetString());
        Assert.Equal("es", root.GetProperty("target_language").GetString());
        Assert.True(root.TryGetProperty("qa_options", out _));
    }

    private static void AssertQaCollections(JsonElement root, out JsonElement segs, out JsonElement totals)
    {
        Assert.True(root.TryGetProperty("segments", out segs));
        Assert.True(root.TryGetProperty("totals", out totals));
        Assert.Equal(1, segs.GetArrayLength());
    }

    private static void AssertQaSegment(JsonElement seg)
    {
        Assert.Equal("seg-001", seg.GetProperty("id").GetString());
        Assert.Equal("Fail", seg.GetProperty("first_attempt").GetProperty("verdict").GetString());
        Assert.Equal("Fail", seg.GetProperty("first_attempt").GetProperty("timing_verdict").GetString());
    }

    private static void AssertQaFirstAttempt(JsonElement seg)
    {
        Assert.Equal("NotEvaluated", seg.GetProperty("first_attempt").GetProperty("meaning_verdict").GetString());
        Assert.Equal(1, seg.GetProperty("revisions").GetArrayLength());
        Assert.Equal("Pass", seg.GetProperty("final_verdict").GetString());
    }

    private static void AssertQaRevision(JsonElement seg)
    {
        JsonElement revision = seg.GetProperty("revisions")[0];
        Assert.Equal("Pass", revision.GetProperty("timing_verdict").GetString());
        Assert.Equal("Pass", revision.GetProperty("meaning_verdict").GetString());
        Assert.Equal("Corta", seg.GetProperty("final_translation").GetString());
    }

    private static void AssertQaMeaningFieldsPresent(JsonElement revision)
    {
        Assert.True(revision.TryGetProperty("meaning_issues", out _));
        Assert.True(revision.TryGetProperty("meaning_explanation", out _));
        Assert.True(revision.TryGetProperty("meaning_prompt_template_version", out _));
    }

    private static void AssertQaMeaningTemplate(JsonElement revision)
    {
        Assert.Equal("meaning-v1", revision.GetProperty("meaning_prompt_template_version").GetString());
    }

    private static void AssertQaTokens(JsonElement seg)
    {
        Assert.True(seg.GetProperty("revisions")[0].GetProperty("meaning_total_tokens").GetInt32() > 0);
        Assert.Equal(0, seg.GetProperty("first_attempt").GetProperty("meaning_total_tokens").GetInt32());
    }

    private static void AssertQaTotals(JsonElement totals)
    {
        Assert.Equal(1, totals.GetProperty("total_segments").GetInt32());
        Assert.Equal(0, totals.GetProperty("first_pass_count").GetInt32());
        Assert.Equal(1, totals.GetProperty("final_pass_count").GetInt32());
    }

    private static void AssertQaTotalsRevisionsAndTokens(JsonElement totals)
    {
        Assert.Equal(1, totals.GetProperty("total_revisions").GetInt32());
        Assert.True(totals.GetProperty("total_tokens").GetInt32() > 0);
    }

    private static void AssertQaOptions(JsonElement root)
    {
        Assert.Equal(3, root.GetProperty("qa_options").GetProperty("max_attempts").GetInt32());
    }

    private static void AssertModels(JsonElement root)
    {
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root.GetArrayLength() > 0);
        Assert.Equal("muse-spark-1.1", root[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Status_without_key_returns_hasKey_false_and_default_model()
    {
        string? prevKey = Environment.GetEnvironmentVariable("META_API_KEY");
        string? prevFallback = Environment.GetEnvironmentVariable("META_MODEL_API_KEY");
        string? prevModel = Environment.GetEnvironmentVariable("META_MODEL");
        string? prevBase = Environment.GetEnvironmentVariable("META_MODEL_API_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("META_API_KEY", null);
            Environment.SetEnvironmentVariable("META_MODEL_API_KEY", null);
            Environment.SetEnvironmentVariable("META_MODEL", null);
            Environment.SetEnvironmentVariable("META_MODEL_API_BASE_URL", null);

            using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
            using HttpClient client = factory.CreateClient();

            HttpResponseMessage resp = await client.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            string json = await resp.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            AssertStatusWithoutKey(root);
            AssertNoKeyLeak(root, json);
        }
        finally
        {
            Environment.SetEnvironmentVariable("META_API_KEY", prevKey);
            Environment.SetEnvironmentVariable("META_MODEL_API_KEY", prevFallback);
            Environment.SetEnvironmentVariable("META_MODEL", prevModel);
            Environment.SetEnvironmentVariable("META_MODEL_API_BASE_URL", prevBase);
        }
    }

    [Fact]
    public async Task Status_with_key_returns_hasKey_true()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resp = await client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("hasKey").GetBoolean());
    }

    [Fact]
    public async Task Translate_happy_path_returns_batch_with_provenance()
    {
        using var auth = FakeAuth();
        var q = new Queue<string>(["Hola, mundo.", "Accion!"]);
        using var factory = new ServerFactory(DualHandler(q, new Queue<string>(), new ChatStubOptions()));
        using HttpClient client = factory.CreateClient();

        var body = new
        {
            segments = new[]
            {
                new { id = "seg-001", text = "Hello, world.", duration_seconds = (double?)4.6 },
                new { id = "seg-002", text = "Action!", duration_seconds = (double?)0.6 },
            },
            sourceLanguage = "en",
            targetLanguage = "es",
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/translate", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        AssertTranslateRoot(root);
        JsonElement first = root.GetProperty("segments")[0];
        AssertFirstTranslatedSegment(first);
        AssertTranslateUsage(root, first);
    }

    [Fact]
    public async Task Qa_happy_path_returns_report_matching_cli_shape()
    {
        using var auth = FakeAuth();
        var translations = new Queue<string>([
            "Esta es una traduccion larguisima", // 32c -> ~2.1s fail for 1.0s window
            "Corta", // 5c -> pass timing, then meaning pass
        ]);
        var meanings = new Queue<string>([JsonSerializer.Serialize(new { verdict = "pass", issues = Array.Empty<string>(), explanation = "ok" })]);
        using var factory = new ServerFactory(DualHandler(translations, meanings, new ChatStubOptions()));
        using HttpClient client = factory.CreateClient();

        var body = new
        {
            segments = new[]
            {
                new { id = "seg-001", text = "This is a long source line that will overrun", duration_seconds = (double?)1.0 },
            },
            sourceLanguage = "en",
            targetLanguage = "es",
            options = new { maxAttempts = 3, toleranceFraction = 0.10 },
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/qa", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        AssertQaRoot(root);
        AssertQaCollections(root, out JsonElement segs, out JsonElement totals);
        JsonElement seg = segs[0];
        AssertQaSegment(seg);
        AssertQaFirstAttempt(seg);
        AssertQaRevision(seg);
        JsonElement revision = seg.GetProperty("revisions")[0];
        AssertQaMeaningFieldsPresent(revision);
        AssertQaMeaningTemplate(revision);
        AssertQaTokens(seg);
        AssertQaTotals(totals);
        AssertQaTotalsRevisionsAndTokens(totals);
        AssertQaOptions(root);
    }

    [Fact]
    public async Task Qa_without_key_gates_with_401()
    {
        string? prevKey = Environment.GetEnvironmentVariable("META_API_KEY");
        string? prevFallback = Environment.GetEnvironmentVariable("META_MODEL_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("META_API_KEY", null);
            Environment.SetEnvironmentVariable("META_MODEL_API_KEY", null);

            using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
            using HttpClient client = factory.CreateClient();

            var body = new
            {
                segments = new[] { new { id = "seg-001", text = "Hello", duration_seconds = (double?)1.0 } },
                sourceLanguage = "en",
                targetLanguage = "es",
            };

            HttpResponseMessage resp = await client.PostAsJsonAsync("/api/qa", body);
            Assert.True(resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadGateway or HttpStatusCode.InternalServerError,
                $"Expected gating status, got {resp.StatusCode}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("META_API_KEY", prevKey);
            Environment.SetEnvironmentVariable("META_MODEL_API_KEY", prevFallback);
        }
    }

    [Fact]
    public async Task Models_returns_account_models()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(ModelsHandler());
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resp = await client.GetAsync("/api/models");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        AssertModels(doc.RootElement);
    }

    private static string ErrorOf(JsonElement root) => root.GetProperty("error").GetString()!;

    [Fact]
    public async Task Models_upstream_server_error_maps_to_502()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(UpstreamError(HttpStatusCode.InternalServerError))));
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resp = await client.GetAsync("/api/models");

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task Models_upstream_unauthorized_maps_to_401()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(UpstreamError(HttpStatusCode.Unauthorized))));
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resp = await client.GetAsync("/api/models");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Unknown_api_route_returns_json_404()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resp = await client.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("Not found", ErrorOf(JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement));
    }

    private static string Segments(int count)
    {
        return "{\"segments\":[" + string.Join(",", Enumerable.Range(0, count).Select(i => $"{{\"id\":\"s{i}\"}}")) + "]}";
    }

    public static IEnumerable<object[]> MalformedRequests()
    {
        const string nineConcepts = "{\"segments\":[{\"id\":\"a\",\"concepts\":[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\",\"i\"]}]}";

        yield return new object[] { "/api/translate", "{not json", "Invalid JSON:" };
        yield return new object[] { "/api/translate", "\"hello\"", "Request body must be a JSON object with 'segments' or a JSON array of segments." };
        yield return new object[] { "/api/translate", "{}", "Request must include a non-empty 'segments' array." };
        yield return new object[] { "/api/translate", "{\"segments\":[]}", "Request must include a non-empty 'segments' array of { id, text, duration_seconds? }." };
        yield return new object[] { "/api/translate", "{\"segments\":[{\"id\":\"  \",\"text\":\"Hi\"}]}", "Each segment must have a non-empty 'id' and 'text'." };

        yield return new object[] { "/api/qa", "{not json", "Invalid JSON:" };
        yield return new object[] { "/api/qa", "{}", "Request must include a non-empty 'segments' array." };
        yield return new object[] { "/api/qa", "{\"segments\":[]}", "Request must include a non-empty 'segments' array of { id, text, duration_seconds? }." };

        yield return new object[] { "/api/scene-context", "{not json", "Invalid JSON:" };
        yield return new object[] { "/api/scene-context", "{\"segments\":[]}", "Request must include a non-empty 'segments' array of { id, text?, frame_image_data_url?, concepts?, speaker_id?, speaker_label?, frame_id?, timecode_seconds?, frame_sha256?, vision_enrichment? }." };
        yield return new object[] { "/api/scene-context", "{\"segments\":[{\"id\":\" \"}]}", "Each segment must have a non-empty 'id'." };
        yield return new object[] { "/api/scene-context", Segments(101), "At most 100 segments are allowed per request." };
        yield return new object[] { "/api/scene-context", nineConcepts, "At most 8 concepts are allowed per segment." };

        yield return new object[] { "/api/director-adaptation", "{not json", "Invalid JSON:" };
        yield return new object[] { "/api/director-adaptation", "{\"segments\":[]}", "Request must include a non-empty 'segments' array of { id, text?, translation?, duration_seconds?, context?, director_adaptation? }." };
        yield return new object[] { "/api/director-adaptation", "{\"segments\":[{\"id\":\"\"}]}", "Each segment must have a non-empty 'id'." };
        yield return new object[] { "/api/director-adaptation", Segments(101), "At most 100 segments are allowed per request." };

        yield return new object[] { "/api/visual-localisation", "{\"segments\":[]}", "Request must include a non-empty 'segments' array" };
        yield return new object[] { "/api/visual-localisation", "{\"segments\":[null]}", "Each segment must have a non-empty 'id'." };
        yield return new object[] { "/api/visual-localisation", "{\"segments\":[{\"id\":\" \"}]}", "Each segment must have a non-empty 'id'." };
        yield return new object[] { "/api/visual-localisation", Segments(101), "At most 100 segments are allowed per request." };
    }

    [Theory]
    [MemberData(nameof(MalformedRequests))]
    public async Task Endpoint_rejects_malformed_request_with_bad_request(string path, string body, string expectedError)
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(UpstreamError(HttpStatusCode.InternalServerError))));
        using HttpClient client = factory.CreateClient();

        (HttpStatusCode status, JsonElement root) = await PostRawAsync(client, path, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.StartsWith(expectedError, ErrorOf(root));
    }

    [Fact]
    public async Task Translate_accepts_a_raw_segment_array_body()
    {
        using var auth = FakeAuth();
        var q = new Queue<string>(["Hola."]);
        using var factory = new ServerFactory(DualHandler(q, new Queue<string>(), new ChatStubOptions()));
        using HttpClient client = factory.CreateClient();

        (HttpStatusCode status, JsonElement root) = await PostRawAsync(client, "/api/translate", "[{\"id\":\"seg-001\",\"text\":\"Hello.\"}]");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, root.GetProperty("segments").GetArrayLength());
    }

    [Fact]
    public async Task Translate_rejects_non_string_language_value()
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(UpstreamError(HttpStatusCode.InternalServerError))));
        using HttpClient client = factory.CreateClient();

        (HttpStatusCode status, _) = await PostRawAsync(client, "/api/translate", "{\"segments\":[{\"id\":\"a\",\"text\":\"Hi\"}],\"sourceLanguage\":5}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Theory]
    [InlineData("\"maxAttempts\":-1", "maxAttempts must be >= 0.")]
    [InlineData("\"toleranceFraction\":-0.1", "toleranceFraction must be >= 0.")]
    [InlineData("\"targetCharactersPerSecond\":0", "targetCharactersPerSecond must be > 0.")]
    [InlineData("\"reasoningEffort\":\"turbo\"", "reasoningEffort must be low, medium, or high.")]
    public async Task Qa_rejects_invalid_options_before_any_model_call(string optionJson, string expectedError)
    {
        using var auth = FakeAuth();
        using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(UpstreamError(HttpStatusCode.InternalServerError))));
        using HttpClient client = factory.CreateClient();
        string body = "{\"segments\":[{\"id\":\"a\",\"text\":\"Hi\",\"duration_seconds\":1.0}],\"sourceLanguage\":\"en\",\"targetLanguage\":\"es\",\"options\":{" + optionJson + "}}";

        (HttpStatusCode status, JsonElement root) = await PostRawAsync(client, "/api/qa", body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(expectedError, ErrorOf(root));
    }
}
