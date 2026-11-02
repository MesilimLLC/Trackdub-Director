using Trackdub.Director.Core;
using Trackdub.Director.Core.SparkOpen;
using static Trackdub.Director.Tests.SparkOpen.SparkOpenTestInfra;

namespace Trackdub.Director.Tests.SparkOpen;

/// <summary>
/// The production runner drives the EXISTING QA loop with Spark Open as the provider; the translation
/// runner grades Spark Open's output with the REAL gate code paths (DurationEstimator for timing and
/// the hosted MeaningGate for meaning). Fake client + fake Meta handler, no live calls.
/// </summary>
[Collection("SparkOpenEnv")]
public sealed class SparkOpenRunnersTests
{
    private static SparkOpenConfig Config() => new("https://spark-open.test.azure/v1", "k", FakeModel, "spark-open.test.azure");

    // ---- Production runner: existing QA loop, Spark Open provider ----

    [Fact]
    public async Task Production_runner_runs_the_voa10_lines_through_the_qa_loop_with_spark_open()
    {
        using var _ = new HostedEnvScope();
        // Spark Open (via the handler) answers translation calls with a plain line and meaning calls
        // with a reasoning-prefixed pass verdict, so the existing QA loop passes each segment.
        var rewriting = new HandlerStub((_, body) => body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal)
            ? ChatResponse(ReasoningMeaningReply(pass: true))
            : ChatResponse("Hola."));
        using var client = new SparkOpenClient(Config(), rewritingInner: rewriting, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));

        QaSegmentInput[] segments =
        [
            new("voa10-001", "Hi, Ashley.", 4.0),
            new("voa10-002", "Okay. See you soon.", 4.0),
        ];
        SparkOpenProductionReport report = await new SparkOpenProductionRunner(client).RunAsync(segments, "en", "es");

        Assert.Equal(nameof(TextProvider.SparkOpen), report.Provider);
        Assert.Equal(FakeModel, report.SparkOpenModel);
        Assert.True(report.FullyGateVerified);
        Assert.Equal(2, report.QaReport.Totals.FinalPassCount);
        Assert.All(report.QaReport.Segments, s => Assert.Equal(FakeModel, s.FirstAttempt!.ModelId));
    }

    [Fact]
    public async Task Production_runner_marks_a_run_not_fully_verified_when_a_line_fails()
    {
        using var _ = new HostedEnvScope();
        // A translation that overruns the tight window and never shrinks fails the timing gate.
        var rewriting = new HandlerStub((_, body) => body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal)
            ? ChatResponse(ReasoningMeaningReply(pass: true))
            : ChatResponse("Una traduccion larguisima que jamas cabe en una ventana tan diminuta de tiempo asignada"));
        using var client = new SparkOpenClient(Config(), rewritingInner: rewriting, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));

        var segments = new[] { new QaSegmentInput("voa10-x", "Hi.", 0.4) };
        SparkOpenProductionReport report = await new SparkOpenProductionRunner(client, new QaOptions { MaxAttempts = 1 }).RunAsync(segments, "en", "es");

        Assert.False(report.FullyGateVerified);
        Assert.Equal(0, report.QaReport.Totals.FinalPassCount);
    }

    [Fact]
    public async Task Production_runner_records_a_failed_generation_and_continues()
    {
        using var _ = new HostedEnvScope();
        // The first line's generation is unextractable (the handler 502s, billing 500 tokens); the
        // second line succeeds. The failed segment is recorded on the report (error + billed usage)
        // and the run still finishes, with the completed segment and totals preserved.
        int calls = 0;
        var rewriting = new HandlerStub((_, body) =>
        {
            calls++;
            return body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal)
                ? ChatResponse(ReasoningMeaningReply(pass: true))
                : calls == 1 ? ChatResponse("<thinking>no answer</thinking>", total: 500) : ChatResponse("Hola.");
        });
        using var client = new SparkOpenClient(Config(), rewritingInner: rewriting, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));

        QaSegmentInput[] segments =
        [
            new("voa10-001", "Hi, Ashley.", 4.0),
            new("voa10-002", "Okay. See you soon.", 4.0),
        ];
        SparkOpenProductionReport report = await new SparkOpenProductionRunner(client).RunAsync(segments, "en", "es");

        Assert.Equal(2, report.QaReport.Segments.Length);
        Assert.Equal(1, report.QaReport.Totals.FailedSegments);
        Assert.Equal(1, report.QaReport.Totals.FinalPassCount);
        Assert.False(report.FullyGateVerified);

        QaSegmentReport failed = report.QaReport.Segments[0];
        Assert.Null(failed.FirstAttempt);
        Assert.Null(failed.FinalTranslation);
        Assert.Equal(QaVerdict.Fail, failed.FinalVerdict);
        Assert.Contains("no extractable final answer", failed.GenerationError, StringComparison.Ordinal);
        Assert.Equal(500, failed.TokenUsage.TotalTokens);

        QaSegmentReport ok = report.QaReport.Segments[1];
        Assert.NotNull(ok.FirstAttempt);
        Assert.Null(ok.GenerationError);
        Assert.Equal(QaVerdict.Pass, ok.FinalVerdict);
    }

    // ---- Translation runner: Spark Open translates, existing gates grade ----

    [Fact]
    public async Task Translation_runner_grades_with_duration_estimator_and_the_hosted_meaning_gate()
    {
        using var _ = new HostedEnvScope();

        // Spark Open translates (plain line through the handler); the hosted client grades meaning.
        var sparkTranslate = new HandlerStub((_, _) => ChatResponse("Hola."));
        using var sparkOpen = new SparkOpenClient(Config(), rewritingInner: sparkTranslate, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));

        // Hosted meaning gate: a plain muse-spark-1.1-shaped JSON verdict (the real MeaningGate parses it).
        int hostedMeaningCalls = 0;
        var hostedMeaning = new HandlerStub((_, body) =>
        {
            Assert.Contains(MeaningGate.MeaningMarker, body, StringComparison.Ordinal);
            hostedMeaningCalls++;
            return ChatResponse("{\"verdict\":\"pass\",\"issues\":[],\"explanation\":\"ok\"}", prompt: 300, completion: 100, total: 400, model: "muse-spark-1.1");
        });
        using var hostedClient = new MetaClient(new HttpClient(hostedMeaning));

        var segments = new[] { new QaSegmentInput("c1", "Hi.", 4.0) };
        var runner = new SparkOpenTranslationRunner(sparkOpen, hostedClient);
        SparkOpenTranslationReport report = await runner.RunAsync(segments, "en", "es");

        SparkOpenTranslationLineResult line = report.Results[0];
        Assert.Equal("Hola.", line.SparkOpenTranslation);
        Assert.Equal(QaVerdict.Pass, line.TimingVerdict);   // DurationEstimator: 5 chars @ 15cps fits 4.0s
        Assert.Equal(MeaningVerdict.Pass, line.MeaningVerdict);
        Assert.Equal(QaVerdict.Pass, line.CombinedVerdict);
        Assert.Equal("muse-spark-1.1", line.MeaningModelId); // meaning graded by the HOSTED gate
        Assert.Equal(1, hostedMeaningCalls);
        Assert.Equal(nameof(TextProvider.SparkOpen), report.TranslationProvider);
        Assert.Equal(nameof(TextProvider.Hosted), report.MeaningProvider);
    }

    [Fact]
    public async Task Translation_runner_fails_a_line_closed_on_timing_without_calling_meaning_pass()
    {
        using var _ = new HostedEnvScope();

        // A long translation overruns the tight window: timing fails, so the combined verdict is Fail.
        // Meaning is still graded for every line, including timing failures.
        var sparkTranslate = new HandlerStub((_, _) => ChatResponse("Una traduccion demasiado larga para una ventana tan breve asignada"));
        using var sparkOpen = new SparkOpenClient(Config(), rewritingInner: sparkTranslate, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));
        int hostedMeaningCalls = 0;
        var hostedMeaning = new HandlerStub((_, body) =>
        {
            Assert.Contains(MeaningGate.MeaningMarker, body, StringComparison.Ordinal);
            hostedMeaningCalls++;
            return ChatResponse("{\"verdict\":\"pass\",\"issues\":[],\"explanation\":\"ok\"}", model: "muse-spark-1.1");
        });
        using var hostedClient = new MetaClient(new HttpClient(hostedMeaning));

        var segments = new[] { new QaSegmentInput("c1", "Hi.", 0.4) };
        SparkOpenTranslationReport report = await new SparkOpenTranslationRunner(sparkOpen, hostedClient).RunAsync(segments, "en", "es");

        SparkOpenTranslationLineResult line = report.Results[0];
        Assert.Equal(QaVerdict.Fail, line.TimingVerdict);
        Assert.Equal(MeaningVerdict.Pass, line.MeaningVerdict);
        Assert.Equal(QaVerdict.Fail, line.CombinedVerdict);
        Assert.Equal(1, hostedMeaningCalls);
    }

    [Fact]
    public async Task Translation_runner_records_a_meaning_error_as_a_fail_and_tallies_it()
    {
        using var _ = new HostedEnvScope();

        var sparkTranslate = new HandlerStub((_, _) => ChatResponse("Hola."));
        using var sparkOpen = new SparkOpenClient(Config(), rewritingInner: sparkTranslate, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));
        // Hosted meaning endpoint errors: the gate records Error; combined verdict is Fail.
        var hostedMeaning = new HandlerStub((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { Content = new StringContent("{}") });
        using var hostedClient = new MetaClient(new HttpClient(hostedMeaning));

        var segments = new[] { new QaSegmentInput("c1", "Hi.", 4.0) };
        SparkOpenTranslationReport report = await new SparkOpenTranslationRunner(sparkOpen, hostedClient).RunAsync(segments, "en", "es");

        Assert.Equal(MeaningVerdict.Error, report.Results[0].MeaningVerdict);
        Assert.Equal(QaVerdict.Fail, report.Results[0].CombinedVerdict);
        Assert.Equal(1, report.Summary.MeaningErrors);
        Assert.Equal(0, report.Summary.CombinedPass);
    }

    [Fact]
    public async Task Translation_runner_carries_the_hosted_side_by_side_translation()
    {
        using var _ = new HostedEnvScope();

        var sparkTranslate = new HandlerStub((_, _) => ChatResponse("Hola, Ashley."));
        using var sparkOpen = new SparkOpenClient(Config(), rewritingInner: sparkTranslate, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));
        var hostedMeaning = new HandlerStub((_, _) => ChatResponse("{\"verdict\":\"pass\",\"issues\":[],\"explanation\":\"ok\"}", model: "muse-spark-1.1"));
        using var hostedClient = new MetaClient(new HttpClient(hostedMeaning));

        var segments = new[] { new QaSegmentInput("voa10-001", "Hi, Ashley.", 4.0) };
        var hosted = new Dictionary<string, string> { ["voa10-001"] = "Hola, Ashley (hosted)." };
        SparkOpenTranslationReport report = await new SparkOpenTranslationRunner(sparkOpen, hostedClient).RunAsync(segments, "en", "es", hosted);

        Assert.Equal("Hola, Ashley (hosted).", report.Results[0].HostedTranslation);
    }

    [Fact]
    public async Task Production_report_names_the_spark_open_endpoint_as_the_meaning_host()
    {
        using var _ = new HostedEnvScope();
        // Meaning checks are served by Spark Open (the redirected transport), so the report must name
        // the Spark Open host, not the hosted endpoint MetaAuth derives.
        var rewriting = new HandlerStub((_, body) => body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal)
            ? ChatResponse(ReasoningMeaningReply(pass: true))
            : ChatResponse("Hola."));
        using var client = new SparkOpenClient(Config(), rewritingInner: rewriting, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));

        var segments = new[] { new QaSegmentInput("voa10-001", "Hi, Ashley.", 4.0) };
        SparkOpenProductionReport report = await new SparkOpenProductionRunner(client).RunAsync(segments, "en", "es");

        QaAttemptReport attempt = report.QaReport.Segments[0].FirstAttempt!;
        Assert.Equal("spark-open.test.azure", attempt.MeaningEndpointHost);
    }

    [Fact]
    public async Task Translation_runner_records_a_failed_generation_and_continues_to_the_next_line()
    {
        using var _ = new HostedEnvScope();
        // First line: the handler 502s (unextractable answer), a billed failed generation. Second line
        // succeeds. The run must record the failed line (with usage) and still finish the others.
        int calls = 0;
        var sparkTranslate = new HandlerStub((_, _) =>
        {
            calls++;
            return calls == 1 ? ChatResponse("<thinking>no answer</thinking>", total: 500) : ChatResponse("Hola.");
        });
        using var sparkOpen = new SparkOpenClient(Config(), rewritingInner: sparkTranslate, rawInner: new HandlerStub((_, _) => ChatResponse("{}")));
        var hostedMeaning = new HandlerStub((_, _) => ChatResponse("{\"verdict\":\"pass\",\"issues\":[],\"explanation\":\"ok\"}", model: "muse-spark-1.1"));
        using var hostedClient = new MetaClient(new HttpClient(hostedMeaning));

        var segments = new[] { new QaSegmentInput("c1", "Hi.", 4.0), new QaSegmentInput("c2", "Bye.", 4.0) };
        SparkOpenTranslationReport report = await new SparkOpenTranslationRunner(sparkOpen, hostedClient).RunAsync(segments, "en", "es");

        Assert.Equal(2, report.Summary.Lines);
        Assert.Equal(1, report.Summary.TranslationErrors);
        Assert.NotNull(report.Results[0].TranslationError);
        Assert.Equal(500, report.Results[0].SparkOpenTranslationUsage?.TotalTokens);
        Assert.Equal(QaVerdict.Fail, report.Results[0].CombinedVerdict);
        Assert.Null(report.Results[1].TranslationError);
        Assert.Equal("Hola.", report.Results[1].SparkOpenTranslation);
    }

    [Fact]
    public async Task Translation_runner_rejects_an_empty_segments_input()
    {
        using var _ = new HostedEnvScope();
        using var sparkOpen = new SparkOpenClient(Config(), rewritingInner: new HandlerStub((_, _) => ChatResponse("Hola.")), rawInner: new HandlerStub((_, _) => ChatResponse("{}")));
        using var hostedClient = new MetaClient(new HttpClient(new HandlerStub((_, _) => ChatResponse("{}"))));

        var runner = new SparkOpenTranslationRunner(sparkOpen, hostedClient);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync([], "en", "es"));
    }
}
