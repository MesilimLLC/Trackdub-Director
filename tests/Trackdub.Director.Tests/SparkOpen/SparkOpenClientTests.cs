using Trackdub.Director.Core;
using Trackdub.Director.Core.SparkOpen;
using static Trackdub.Director.Tests.SparkOpen.SparkOpenTestInfra;

namespace Trackdub.Director.Tests.SparkOpen;

/// <summary>
/// The real client and handler end-to-end over scripted transports (no socket opened):
/// a reasoning-prefixed reply parses to its final answer for both the judge and the QA/translation
/// paths, an unextractable reply fails closed, and a missing config makes zero calls.
/// </summary>
public sealed class SparkOpenClientTests
{
    private static SparkOpenConfig Config() => new("https://spark-open.test.azure/v1", "test-key", FakeModel, "spark-open.test.azure");

    [Fact]
    public async Task Judge_parses_a_reasoning_prefixed_verdict_and_preserves_usage()
    {
        var raw = new HandlerStub((_, _) => ChatResponse(ReasoningMeaningReply(pass: false, explanation: "number changed", "number"), total: 1600));
        using var client = new SparkOpenClient(Config(), rewritingInner: new HandlerStub((_, _) => ChatResponse("{}")), rawInner: raw);

        MeaningCheckResult result = await client.JudgeMeaningAsync("We need one more take.", "Necesitamos dos tomas.", "en", "es");

        Assert.Equal(MeaningVerdict.Fail, result.Verdict);
        Assert.Equal(1600, result.Usage?.TotalTokens);
        Assert.Equal(1, raw.Calls);
    }

    [Fact]
    public async Task Judge_fails_closed_to_Error_when_no_verdict_can_be_extracted()
    {
        // Reasoning only, no JSON verdict: a failed judgement (Error), never a guessed Pass/Fail.
        var raw = new HandlerStub((_, _) => ChatResponse("<think>I am still deliberating and never answer.</think>\nStill thinking.", total: 900));
        using var client = new SparkOpenClient(Config(), rewritingInner: new HandlerStub((_, _) => ChatResponse("{}")), rawInner: raw);

        MeaningCheckResult result = await client.JudgeMeaningAsync("Hi.", "Hola.", "en", "es");

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

        MeaningCheckResult result = await client.JudgeMeaningAsync("Hi.", "Hola.", "en", "es");
        Assert.Equal(MeaningVerdict.Error, result.Verdict);
    }

    [Fact]
    public async Task QA_translation_path_surfaces_the_plain_final_line_as_the_translation()
    {
        // MetaClient requires some hosted key to build a request; the handler overwrites the
        // Authorization header with the Spark Open key, so the hosted key is never sent to Spark Open.
        using var _ = new HostedEnvScope();
        // The handler must hand the QA loop the plain translated line, not a JSON wrapper.
        var rewriting = new HandlerStub((_, _) => ChatResponse("<think>Keep the name.</think>\nHola, Ashley."));
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
        var rewriting = new HandlerStub((_, _) => ChatResponse("<think>no answer</think>"));
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
        using var _ = new SparkOpenEnvScope(baseUrl, key, model);
        Assert.False(SparkOpenAuth.IsConfigured());
        Assert.Throws<SparkOpenConfigException>(() => SparkOpenAuth.Resolve());
    }

    [Fact]
    public void A_non_absolute_base_url_fails_closed()
    {
        using var _ = new SparkOpenEnvScope(baseUrl: "not-a-url", apiKey: "k", model: "m");
        Assert.Throws<SparkOpenConfigException>(() => SparkOpenAuth.Resolve());
    }
}
