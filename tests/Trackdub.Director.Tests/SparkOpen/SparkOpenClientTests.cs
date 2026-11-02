using Trackdub.Director.Core;
using Trackdub.Director.Core.SparkOpen;
using static Trackdub.Director.Tests.SparkOpen.SparkOpenTestInfra;

namespace Trackdub.Director.Tests.SparkOpen;

/// <summary>
/// The real client and handler end-to-end over scripted transports (no socket opened):
/// a reasoning-prefixed reply parses to its final answer for both the judge and the QA/translation
/// paths, an unextractable reply fails closed, and a missing config makes zero calls.
/// </summary>
[Collection("SparkOpenEnv")]
public sealed class SparkOpenClientTests
{
    private static SparkOpenConfig Config() => new("https://spark-open.test.azure/v1", "test-key", FakeModel, "spark-open.test.azure");

    [Fact]
    public async Task Judge_parses_a_reasoning_prefixed_verdict_and_preserves_usage()
    {
        var raw = new HandlerStub((_, _) => ChatResponse(ReasoningMeaningReply(pass: false, explanation: "number changed", "number"), usage: new ChatUsage(Total: 1600)));
        using var client = new SparkOpenClient(Config(), rewritingInner: new HandlerStub((_, _) => ChatResponse("{}")), rawInner: raw);

        MeaningCheckResult result = await client.JudgeMeaningAsync(
            new JudgeMeaningRequest("We need one more take.", "Necesitamos dos tomas.", "en", "es"));

        Assert.Equal(MeaningVerdict.Fail, result.Verdict);
        Assert.Equal(1600, result.Usage?.TotalTokens);
        Assert.Equal(1, raw.Calls);
    }

    [Fact]
    public async Task Judge_fails_closed_to_Error_when_no_verdict_can_be_extracted()
    {
        // Reasoning only, no JSON verdict: a failed judgement (Error), never a guessed Pass/Fail.
        var raw = new HandlerStub((_, _) => ChatResponse("<thinking>I am still deliberating and never answer.</thinking>\nStill thinking.", usage: new ChatUsage(Total: 900)));
        using var client = new SparkOpenClient(Config(), rewritingInner: new HandlerStub((_, _) => ChatResponse("{}")), rawInner: raw);

        MeaningCheckResult result = await client.JudgeMeaningAsync(new JudgeMeaningRequest("Hi.", "Hola.", "en", "es"));

        Assert.Equal(MeaningVerdict.Error, result.Verdict);
        Assert.Equal(900, result.Usage?.TotalTokens);
    }

    [Fact]
    public async Task Judge_records_Error_on_a_non_success_response()
    {
        var raw = new HandlerStub((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{}"),
        });
        using var client = new SparkOpenClient(Config(), rewritingInner: new HandlerStub((_, _) => ChatResponse("{}")), rawInner: raw);

        MeaningCheckResult result = await client.JudgeMeaningAsync(new JudgeMeaningRequest("Hi.", "Hola.", "en", "es"));
        Assert.Equal(MeaningVerdict.Error, result.Verdict);
    }

    [Fact]
    public async Task Judge_records_the_azure_error_message_in_the_failed_call_reason()
    {
        // Azure error bodies carry a message field; the recorded reason must keep it so the report
        // shows why the judgement failed, not just the status code.
        var raw = new HandlerStub((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)
        {
            Content = new StringContent("{\"error\":{\"message\":\"Deployment 'spark-open-816b' is not found.\"}}"),
        });
        using var client = new SparkOpenClient(Config(), rewritingInner: new HandlerStub((_, _) => ChatResponse("{}")), rawInner: raw);

        MeaningCheckResult result = await client.JudgeMeaningAsync(new JudgeMeaningRequest("Hi.", "Hola.", "en", "es"));

        Assert.Equal(MeaningVerdict.Error, result.Verdict);
        Assert.Contains("Deployment 'spark-open-816b' is not found.", result.Explanation);
    }

    [Fact]
    public async Task QA_translation_path_surfaces_the_plain_final_line_as_the_translation()
    {
        // MetaClient requires some hosted key to build a request; the handler overwrites the
        // Authorization header with the Spark Open key, so the hosted key is never sent to Spark Open.
        using var _ = new HostedEnvScope();
        // The handler must hand the QA loop the plain translated line, not a JSON wrapper.
        var rewriting = new HandlerStub((_, _) => ChatResponse("<thinking>Keep the name.</thinking>\nHola, Ashley."));
        using var client = new SparkOpenClient(Config(), rewritingInner: rewriting, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));

        MetaClient.ChatCompletionResult result = await client.AsMetaClient().TranslateWithUsageAsync(
            new MetaClient.TranslationRequest("Hi, Ashley.", "en", "es"));

        Assert.Equal("Hola, Ashley.", result.Content);
        Assert.Equal(1, rewriting.Calls);
    }

    [Fact]
    public async Task QA_path_redirects_to_the_spark_open_endpoint_and_swaps_model_and_key()
    {
        using var _ = new HostedEnvScope();
        Uri? seenUri = null;
        string? seenAuth = null;
        var rewriting = new HandlerStub((req, _) =>
        {
            seenUri = req.RequestUri;
            seenAuth = req.Headers.Authorization?.ToString();
            return ChatResponse("Hola.");
        });
        using var client = new SparkOpenClient(Config(), rewritingInner: rewriting, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));

        await client.AsMetaClient().TranslateWithUsageAsync(new MetaClient.TranslationRequest("Hi.", "en", "es"));

        Assert.Equal("https://spark-open.test.azure/v1/chat/completions", seenUri?.ToString());
        Assert.Equal("Bearer test-key", seenAuth);
        Assert.Contains($"\"model\":\"{FakeModel}\"", rewriting.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("muse-spark-1.1", rewriting.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task QA_meaning_check_parses_a_reasoning_prefixed_verdict_through_the_handler()
    {
        using var _ = new HostedEnvScope();
        // The existing MeaningGate path, driven through the handler, extracts the verdict from reasoning.
        var rewriting = new HandlerStub((_, body) => body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal)
            ? ChatResponse(ReasoningMeaningReply(pass: true))
            : ChatResponse("Hola."));
        using var client = new SparkOpenClient(Config(), rewritingInner: rewriting, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));

        MeaningCheckResult meaning = await client.AsMetaClient().CheckMeaningAsync(
            new MetaClient.MeaningCheckRequest("Hi.", "Hola.", "en", "es"));

        Assert.Equal(MeaningVerdict.Pass, meaning.Verdict);
    }

    [Fact]
    public async Task QA_path_fails_a_call_whose_reply_has_no_extractable_answer()
    {
        using var _ = new HostedEnvScope();
        var rewriting = new HandlerStub((_, _) => ChatResponse("<thinking>no answer</thinking>"));
        using var client = new SparkOpenClient(Config(), rewritingInner: rewriting, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));

        // MetaClient surfaces the handler's 502 as a MetaApiException: a failed call, not a guess.
        await Assert.ThrowsAsync<MetaApiException>(() =>
            client.AsMetaClient().TranslateWithUsageAsync(new MetaClient.TranslationRequest("Hi.", "en", "es")));
    }

    [Fact]
    public void Missing_env_config_fails_closed_with_zero_calls()
    {
        using var _ = new ClearedSparkOpenEnvScope();

        // Resolving the route throws before any HttpClient is built or any request is sent.
        Assert.False(SparkOpenAuth.IsConfigured());
        Assert.Throws<SparkOpenConfigException>(() => SparkOpenAuth.Resolve());
        Assert.Throws<SparkOpenConfigException>(() => new SparkOpenClient());
    }

    [Theory]
    [InlineData(null, "key", "model")]
    [InlineData("https://x/v1", null, "model")]
    [InlineData("https://x/v1", "key", null)]
    public void Any_missing_env_var_fails_closed(string? baseUrl, string? key, string? model)
    {
        using var _ = new SparkOpenEnvScope(new SparkOpenEnv { BaseUrl = baseUrl, ApiKey = key, Model = model });
        Assert.False(SparkOpenAuth.IsConfigured());
        Assert.Throws<SparkOpenConfigException>(() => SparkOpenAuth.Resolve());
    }

    [Fact]
    public void A_non_absolute_base_url_fails_closed()
    {
        using var _ = new SparkOpenEnvScope(new SparkOpenEnv { BaseUrl = "not-a-url", ApiKey = "k", Model = "m" });
        Assert.Throws<SparkOpenConfigException>(() => SparkOpenAuth.Resolve());
    }

    [Fact]
    public async Task QA_path_unwraps_a_translation_wrapped_json_object_to_the_plain_line()
    {
        using var _ = new HostedEnvScope();
        // The injected instruction asks for a JSON final object, so Spark Open may reply with
        // {"translation":"..."}; the handler must unwrap it so timing estimates, meaning checks,
        // and saved translations see the plain line, never the wrapper.
        var rewriting = new HandlerStub((_, _) => ChatResponse(ReasoningTranslationReply("Hola, Ashley.")));
        using var client = new SparkOpenClient(Config(), rewritingInner: rewriting, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));

        MetaClient.ChatCompletionResult result = await client.AsMetaClient().TranslateWithUsageAsync(
            new MetaClient.TranslationRequest("Hi, Ashley.", "en", "es"));

        Assert.Equal("Hola, Ashley.", result.Content);
    }

    [Fact]
    public async Task QA_path_keeps_a_meaning_verdict_that_also_carries_a_translation_string()
    {
        using var _ = new HostedEnvScope();
        // A meaning reply that carries a translation string next to the verdict must not be unwrapped
        // to that string (which would make MeaningGate parse it as a Fail); the verdict object stays.
        var rewriting = new HandlerStub((_, body) => body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal)
            ? ChatResponse("{\"verdict\":\"pass\",\"issues\":[],\"explanation\":\"ok\",\"translation\":\"Hola\"}")
            : ChatResponse("Hola."));
        using var client = new SparkOpenClient(Config(), rewritingInner: rewriting, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));

        MeaningCheckResult meaning = await client.AsMetaClient().CheckMeaningAsync(
            new MetaClient.MeaningCheckRequest("Hi.", "Hola.", "en", "es"));

        Assert.Equal(MeaningVerdict.Pass, meaning.Verdict);
    }

    [Fact]
    public async Task QA_path_fails_a_translation_reply_that_is_a_json_object_without_the_translation_key()
    {
        using var _ = new HostedEnvScope();
        // The final-answer instruction names no key; a {"answer":"..."} reply must not reach timing
        // estimates, meaning checks, or the saved report as raw JSON — it fails the call instead.
        var rewriting = new HandlerStub((_, _) => ChatResponse("{\"answer\":\"Hola\"}"));
        using var client = new SparkOpenClient(Config(), rewritingInner: rewriting, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));

        await Assert.ThrowsAsync<MetaApiException>(() =>
            client.AsMetaClient().TranslateWithUsageAsync(new MetaClient.TranslationRequest("Hi.", "en", "es")));
    }

    [Fact]
    public void A_plaintext_http_endpoint_fails_closed()
    {
        using var _ = new SparkOpenEnvScope(new SparkOpenEnv { BaseUrl = "http://deployment.example/v1", ApiKey = "k", Model = "m" });
        Assert.Throws<SparkOpenConfigException>(() => SparkOpenAuth.Resolve());
    }

    [Fact]
    public void An_injected_config_with_a_plaintext_non_loopback_route_fails_closed()
    {
        // The injected-transport constructor must enforce the same https-or-loopback rule the env path
        // does, so a caller-supplied config cannot send the Bearer key over plaintext.
        var config = new SparkOpenConfig("http://deployment.example/v1", "k", FakeModel, "deployment.example");
        Assert.Throws<SparkOpenConfigException>(() =>
            new SparkOpenClient(config, new HandlerStub((_, _) => ChatResponse("{}")), new HandlerStub((_, _) => ChatResponse("{}"))));
    }

    [Fact]
    public void A_loopback_http_endpoint_is_allowed()
    {
        using var _ = new SparkOpenEnvScope(new SparkOpenEnv { BaseUrl = "http://localhost:8000/v1", ApiKey = "k", Model = "m" });
        SparkOpenConfig config = SparkOpenAuth.Resolve();
        Assert.Equal("localhost", config.EndpointHost);
    }

    [Fact]
    public void A_base_url_with_a_query_or_fragment_fails_closed()
    {
        using var _ = new SparkOpenEnvScope(new SparkOpenEnv { BaseUrl = "https://host/v1?api-version=2024-01-01", ApiKey = "k", Model = "m" });
        Assert.Throws<SparkOpenConfigException>(() => SparkOpenAuth.Resolve());
    }

    [Fact]
    public async Task Judge_fails_closed_to_Error_on_an_unknown_verdict()
    {
        // A verdict that is neither pass nor fail is a failed judgement, never a normalized Fail that
        // could count as agreement with a hosted Fail.
        var raw = new HandlerStub((_, _) => ChatResponse("<thinking>I considered.</thinking>\n{\"verdict\":\"banana\",\"issues\":[],\"explanation\":\"x\"}", usage: new ChatUsage(Total: 700)));
        using var client = new SparkOpenClient(Config(), rewritingInner: new HandlerStub((_, _) => ChatResponse("{}")), rawInner: raw);

        MeaningCheckResult result = await client.JudgeMeaningAsync(new JudgeMeaningRequest("Hi.", "Hola.", "en", "es"));

        Assert.Equal(MeaningVerdict.Error, result.Verdict);
        Assert.Equal(700, result.Usage?.TotalTokens);
    }

    [Fact]
    public async Task Judge_fails_closed_to_Error_on_a_malformed_issues_array()
    {
        var raw = new HandlerStub((_, _) => ChatResponse("{\"verdict\":\"pass\",\"issues\":\"not-an-array\",\"explanation\":\"x\"}"));
        using var client = new SparkOpenClient(Config(), rewritingInner: new HandlerStub((_, _) => ChatResponse("{}")), rawInner: raw);

        MeaningCheckResult result = await client.JudgeMeaningAsync(new JudgeMeaningRequest("Hi.", "Hola.", "en", "es"));

        Assert.Equal(MeaningVerdict.Error, result.Verdict);
    }

    [Fact]
    public async Task Judge_fails_closed_to_Error_when_the_verdict_is_nested_in_a_truncated_wrapper()
    {
        // The reply is truncated inside an outer object; the inner verdict must not be scored as the
        // final top-level verdict (the completed wrapper has no verdict property and is an Error).
        var raw = new HandlerStub((_, _) => ChatResponse("{\"draft\":{\"verdict\":\"pass\",\"issues\":[]}", usage: new ChatUsage(Total: 650)));
        using var client = new SparkOpenClient(Config(), rewritingInner: new HandlerStub((_, _) => ChatResponse("{}")), rawInner: raw);

        MeaningCheckResult result = await client.JudgeMeaningAsync(new JudgeMeaningRequest("Hi.", "Hola.", "en", "es"));

        Assert.Equal(MeaningVerdict.Error, result.Verdict);
        Assert.Equal(650, result.Usage?.TotalTokens);
    }

    [Fact]
    public async Task QA_failed_meaning_reply_preserves_billed_usage_in_the_failure_contract()
    {
        using var _ = new HostedEnvScope();
        // The handler's 502 body carries the billed usage; MetaClient must surface it on the exception
        // so a failed meaning reply is not accounted as zero tokens.
        var rewriting = new HandlerStub((_, body) => body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal)
            ? ChatResponse("<thinking>no answer</thinking>", usage: new ChatUsage(Total: 900))
            : ChatResponse("Hola."));
        using var client = new SparkOpenClient(Config(), rewritingInner: rewriting, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() =>
            client.AsMetaClient().CheckMeaningAsync(new MetaClient.MeaningCheckRequest("Hi.", "Hola.", "en", "es")));

        Assert.Equal(900, ex.Usage?.TotalTokens);
    }

    [Fact]
    public async Task Dispose_with_caller_owned_transports_leaves_the_transports_alive()
    {
        using var _ = new HostedEnvScope();
        var rewriting = new HandlerStub((_, _) => ChatResponse("Hola."));

        var client = new SparkOpenClient(Config(), rewritingInner: rewriting, rawInner: new HandlerStub((_, _) => ChatResponse("{}")), ownsHttpClients: false);
        client.Dispose();

        // The caller supplied the transports, so disposal must not take them down.
        using var http = new HttpClient(rewriting);
        HttpResponseMessage response = await http.GetAsync("https://spark-open.test.azure/v1/chat/completions");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task QA_path_runs_without_any_hosted_credentials()
    {
        using var _ = new ClearedHostedEnvScope();
        // Only SPARKOPEN_* is configured: the Spark Open adapter must never need META_API_KEY.
        var rewriting = new HandlerStub((_, _) => ChatResponse("Hola."));
        using var client = new SparkOpenClient(Config(), rewritingInner: rewriting, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));

        MetaClient.ChatCompletionResult result = await client.AsMetaClient().TranslateWithUsageAsync(
            new MetaClient.TranslationRequest("Hi.", "en", "es"));

        Assert.Equal("Hola.", result.Content);
    }
}
