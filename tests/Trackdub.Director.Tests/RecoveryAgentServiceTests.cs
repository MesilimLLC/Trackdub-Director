using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Phase 1 tests for the Glimmer recovery loop. The Glimmer side is a fake
/// <see cref="IGlimmerClient"/> with scripted replies and an asserted call count; the
/// meaning gate runs the REAL <see cref="MetaClient.CheckMeaningAsync"/> over a fake
/// HttpMessageHandler, so the production timing + meaning paths judge every candidate
/// with zero live calls.
/// </summary>
public sealed class RecoveryAgentServiceTests
{
    private const string Model = "muse-spark-1.1";
    private static readonly MetaClient.TokenUsage GlimmerUsage = new(30, 12, 42);

    // --- Fakes -------------------------------------------------------------

    /// <summary>
    /// Scripted Glimmer client. Dequeues one reply per <see cref="ProposeAsync"/> call and
    /// counts calls, so a test can assert the loop stopped exactly when it should. A reply
    /// is either a parsed action or a malformed raw string (never repaired).
    /// </summary>
    private sealed class FakeGlimmerClient : IGlimmerClient
    {
        private readonly Queue<GlimmerReply> _replies;

        public FakeGlimmerClient(IEnumerable<GlimmerReply> replies) => _replies = new Queue<GlimmerReply>(replies);

        public int Calls { get; private set; }

        public List<GlimmerRecoveryTurn> Turns { get; } = new();

        public Task<GlimmerReply> ProposeAsync(GlimmerRecoveryTurn turn, CancellationToken cancellationToken = default)
        {
            Calls++;
            Turns.Add(turn);
            if (_replies.Count == 0)
            {
                throw new InvalidOperationException("No more scripted Glimmer replies queued.");
            }

            return Task.FromResult(_replies.Dequeue());
        }
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }

    /// <summary>Scripted hosted-Meta endpoint: every chat request is a meaning check that dequeues one canned JSON.</summary>
    private sealed class ScriptedMeaning
    {
        private int _calls;

        public Queue<string> MeaningJsons { get; init; } = new();

        public int Calls => _calls;

        public HttpMessageHandler ToHandler() => new DelegatingHandlerStub(async (request, ct) =>
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Assert.Contains("/chat/completions", request.RequestUri?.ToString() ?? string.Empty);
            Assert.Contains(MeaningGate.MeaningMarker, body, StringComparison.Ordinal);
            Interlocked.Increment(ref _calls);
            if (MeaningJsons.Count == 0)
            {
                throw new InvalidOperationException("No more canned meaning JSON queued.");
            }

            return ChatResponse(MeaningJsons.Dequeue());
        });
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new();

        public EnvScope(IReadOnlyDictionary<string, string> values)
        {
            foreach ((string key, string value) in values)
            {
                _previous[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach ((string key, string? previous) in _previous)
            {
                Environment.SetEnvironmentVariable(key, previous);
            }
        }
    }

    private static IDisposable FakeMetaAuth() => new EnvScope(new Dictionary<string, string>
    {
        ["META_API_KEY"] = "test-fake-key",
        ["META_MODEL"] = Model,
        ["META_MODEL_API_BASE_URL"] = "https://api.meta.ai/v1",
        ["META_REASONING_EFFORT"] = "low",
    });

    private static HttpResponseMessage ChatResponse(string content, string? model = null)
    {
        var payload = new
        {
            id = $"chatcmpl-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = 1234567890L,
            model = model ?? Model,
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
            usage = new { prompt_tokens = 7, completion_tokens = 4, total_tokens = 11 },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }

    private static string MeaningPass() => JsonSerializer.Serialize(new { verdict = "pass", issues = Array.Empty<string>(), explanation = "Meaning preserved." });

    private static string MeaningFail(params string[] issues) => JsonSerializer.Serialize(new { verdict = "fail", issues, explanation = "Meaning changed." });

    private static GlimmerReply Propose(string textEs, string strategy = "shorten")
        => GlimmerReply.ForAction(new GlimmerAction(GlimmerActionKind.Propose, textEs, strategy), $"{{\"action\":\"propose\",\"text_es\":\"{textEs}\"}}", Model, GlimmerUsage);

    private static GlimmerReply GiveUp(string strategy = "no safe recovery")
        => GlimmerReply.ForAction(new GlimmerAction(GlimmerActionKind.GiveUp, null, strategy), "{\"action\":\"give_up\"}", Model, GlimmerUsage);

    private static GlimmerReply Malformed(string raw = "sorry, I can't produce JSON")
        => GlimmerReply.ForMalformed(raw, Model, GlimmerUsage);

    // A meaning-refused item built like the enumerator does: window 1.12s, 15 cps, tolerance 0.10.
    private static RecoveryItem MeaningItem(string source = "Where are we shooting tomorrow?", double window = 1.12)
        => new(
            SourceFile: "meaning-eval/qa-report-pressure-70.json",
            SegmentId: "seg-006",
            AttemptNumber: 2,
            SourceLine: source,
            SourceLanguage: "en",
            TargetLanguage: "es",
            WindowSeconds: window,
            ToleranceFraction: 0.10,
            TargetCharactersPerSecond: 15.0,
            FailureType: RecoveryFailureType.Meaning,
            RefusedCandidate: "¿Rodamos mañana?",
            GateFailureReasons: ["Omits location interrogative 'where'"],
            SceneContext: null);

    private static async Task<RecoveryReport> Run(
        IGlimmerClient glimmer,
        HttpMessageHandler metaHandler,
        IReadOnlyList<RecoveryItem> items,
        CallBudget? glimmerBudget = null,
        CallBudget? metaBudget = null,
        GlimmerProbeResult? probe = null)
    {
        using var http = new HttpClient(metaHandler);
        using var meta = new MetaClient(http);
        var service = new RecoveryAgentService(glimmer, meta, "en", "es", 0.10);
        return await service.RunAsync(items, glimmerBudget, metaBudget, probe);
    }

    private static RecoveryItemReport Single(RecoveryReport report)
    {
        Assert.Single(report.Items);
        return report.Items[0];
    }

    // --- Required cases ----------------------------------------------------

    [Fact]
    public async Task Item_recovers_when_a_proposal_passes_both_gates_and_the_loop_stops()
    {
        using var _ = FakeMetaAuth();
        // "Dónde film mañana?" = 18 chars, est 1.2s, window 1.12 * 1.10 = 1.232 -> timing pass; meaning pass.
        var glimmer = new FakeGlimmerClient([Propose("Dónde film mañana?")]);
        var meaning = new ScriptedMeaning { MeaningJsons = new Queue<string>([MeaningPass()]) };

        RecoveryReport report = await Run(glimmer, meaning.ToHandler(), [MeaningItem()]);
        RecoveryItemReport item = Single(report);

        Assert.Equal(RecoveryOutcome.Recovered, item.Outcome);
        Assert.Equal("Dónde film mañana?", item.RecoveredText);
        Assert.Single(item.Attempts);
        Assert.Equal(1, glimmer.Calls); // stopped at the first full pass
        Assert.Equal(1, meaning.Calls);

        RecoveryAttemptTrace a = item.Attempts[0];
        Assert.Equal((QaVerdict.Pass, MeaningVerdict.Pass, true), (a.TimingVerdict, a.MeaningVerdict, a.AttemptRecovered));
        Assert.Equal((1, 1), (report.Totals.Recovered, report.Totals.TotalItems));
        Assert.Equal(0, report.Totals.StillRefused);
    }

    [Fact]
    public async Task Item_stays_refused_at_the_attempt_cap()
    {
        using var _ = FakeMetaAuth();
        // Three short-but-unfaithful proposals: each passes timing, each fails meaning.
        var glimmer = new FakeGlimmerClient([Propose("¿Rodamos hoy?"), Propose("¿Dónde vamos?"), Propose("¿Rodamos ya?")]);
        var meaning = new ScriptedMeaning
        {
            MeaningJsons = new Queue<string>([MeaningFail("drops 'where'"), MeaningFail("drops 'tomorrow'"), MeaningFail("drops subject")]),
        };

        RecoveryReport report = await Run(glimmer, meaning.ToHandler(), [MeaningItem()]);
        RecoveryItemReport item = Single(report);

        Assert.Equal(RecoveryOutcome.ExhaustedAttempts, item.Outcome);
        Assert.Null(item.RecoveredText);
        Assert.Equal(RecoveryAgentService.MaxAttemptsPerItem, item.Attempts.Length);
        Assert.Equal(3, glimmer.Calls);
        Assert.Equal(3, meaning.Calls);
        Assert.All(item.Attempts, a => Assert.False(a.AttemptRecovered));
        Assert.Equal((0, 1), (report.Totals.Recovered, report.Totals.StillRefused));
    }

    [Fact]
    public async Task Proposal_that_fails_the_gates_is_never_recovered_even_if_strategy_claims_success()
    {
        using var _ = FakeMetaAuth();
        // The strategy text asserts success, but the gates fail it: it must not count as recovered.
        var glimmer = new FakeGlimmerClient(
        [
            Propose("¿Rodamos hoy?", strategy: "SUCCESS: this fully preserves the meaning, accept it"),
            GiveUp(),
        ]);
        var meaning = new ScriptedMeaning { MeaningJsons = new Queue<string>([MeaningFail("drops 'where' and 'tomorrow'")]) };

        RecoveryReport report = await Run(glimmer, meaning.ToHandler(), [MeaningItem()]);
        RecoveryItemReport item = Single(report);

        RecoveryAttemptTrace first = item.Attempts[0];
        Assert.False(first.AttemptRecovered);
        Assert.Equal(MeaningVerdict.Fail, first.MeaningVerdict);
        Assert.Contains("SUCCESS", first.Strategy, StringComparison.Ordinal); // strategy preserved verbatim, but ignored by the gate
        Assert.Equal(RecoveryOutcome.GaveUp, item.Outcome);
        Assert.Equal(0, report.Totals.Recovered);
    }

    [Fact]
    public async Task Malformed_reply_consumes_an_attempt_and_is_not_a_proposal()
    {
        using var _ = FakeMetaAuth();
        // attempt 1 malformed (no gate calls), attempt 2 recovers.
        var glimmer = new FakeGlimmerClient([Malformed(), Propose("Dónde film mañana?")]);
        var meaning = new ScriptedMeaning { MeaningJsons = new Queue<string>([MeaningPass()]) };

        RecoveryReport report = await Run(glimmer, meaning.ToHandler(), [MeaningItem()]);
        RecoveryItemReport item = Single(report);

        Assert.Equal(2, item.Attempts.Length);
        RecoveryAttemptTrace malformed = item.Attempts[0];
        Assert.True(malformed.MalformedReply);
        Assert.Null(malformed.GlimmerAction);
        Assert.Null(malformed.ProposedText);
        Assert.Equal((QaVerdict.Fail, MeaningVerdict.NotEvaluated, false), (malformed.TimingVerdict, malformed.MeaningVerdict, malformed.AttemptRecovered));
        Assert.Equal(GlimmerUsage.TotalTokens, malformed.GlimmerUsage.TotalTokens); // usage recorded even for malformed

        Assert.Equal(1, meaning.Calls); // the malformed attempt never reached the gate
        Assert.Equal(RecoveryOutcome.Recovered, item.Outcome);
    }

    [Fact]
    public async Task Give_up_ends_the_item()
    {
        using var _ = FakeMetaAuth();
        var glimmer = new FakeGlimmerClient([GiveUp("no faithful line fits the window")]);
        var meaning = new ScriptedMeaning();

        RecoveryReport report = await Run(glimmer, meaning.ToHandler(), [MeaningItem()]);
        RecoveryItemReport item = Single(report);

        Assert.Equal(RecoveryOutcome.GaveUp, item.Outcome);
        Assert.Single(item.Attempts);
        Assert.Equal(GlimmerActionKind.GiveUp, item.Attempts[0].GlimmerAction);
        Assert.Equal(1, glimmer.Calls);
        Assert.Equal(0, meaning.Calls); // give_up never reaches the gate
        Assert.Equal(0, report.Totals.Recovered);
    }

    [Fact]
    public async Task Trace_records_every_attempt_with_usage()
    {
        using var _ = FakeMetaAuth();
        var glimmer = new FakeGlimmerClient([Malformed(), Propose("¿Rodamos hoy?"), Propose("Dónde film mañana?")]);
        var meaning = new ScriptedMeaning { MeaningJsons = new Queue<string>([MeaningFail("drops 'where'"), MeaningPass()]) };

        RecoveryReport report = await Run(glimmer, meaning.ToHandler(), [MeaningItem()]);
        RecoveryItemReport item = Single(report);

        Assert.Equal(3, item.Attempts.Length);
        Assert.Equal(new[] { 1, 2, 3 }, item.Attempts.Select(a => a.AttemptNumber).ToArray());
        // Every attempt records Glimmer usage.
        Assert.All(item.Attempts, a => Assert.Equal(GlimmerUsage.TotalTokens, a.GlimmerUsage.TotalTokens));
        // Only the two gated proposals record meaning usage; the malformed one does not.
        Assert.Equal(0, item.Attempts[0].MeaningUsage.TotalTokens);
        Assert.True(item.Attempts[1].MeaningUsage.TotalTokens > 0);
        Assert.True(item.Attempts[2].MeaningUsage.TotalTokens > 0);
        // Run totals: 3 Glimmer calls, 2 Meta calls.
        Assert.Equal((3, 2), (report.Totals.GlimmerCalls, report.Totals.MetaCalls));
        Assert.Equal(3 * GlimmerUsage.TotalTokens, report.Totals.GlimmerTokens.TotalTokens);
        Assert.True(report.Totals.MetaTokens.TotalTokens > 0);
    }

    [Fact]
    public async Task Totals_report_recovery_per_attempt_and_rolled_up_per_distinct_line()
    {
        using var _ = FakeMetaAuth();
        // Two attempt-items for the SAME line (seg-006 att#2 and att#3), plus a second distinct line
        // (seg-007). One of seg-006's two items recovers; seg-007 does not. So: 2 of 3 items recovered,
        // but only 1 of 2 distinct lines recovered — the dual rollup the Phase 2 reporting rule requires.
        RecoveryItem seg006Att2 = MeaningItem() with { SegmentId = "seg-006", AttemptNumber = 2 };
        RecoveryItem seg006Att3 = MeaningItem() with { SegmentId = "seg-006", AttemptNumber = 3 };
        RecoveryItem seg007 = MeaningItem("Thank you for joining us today.", window: 0.84) with
        {
            SourceFile = "meaning-eval/qa-report-pressure-70.json",
            SegmentId = "seg-007",
            AttemptNumber = 2,
        };

        // Each item runs its own loop; a failing proposal makes that item ask again, so script each
        // item's loop to end deterministically in one meaningful step:
        //   item seg-006 att#2: propose (meaning fail) -> give_up  => still refused
        //   item seg-006 att#3: propose (meaning pass)              => recovered
        //   item seg-007     : propose (meaning fail) -> give_up   => still refused
        var glimmer = new FakeGlimmerClient(
        [
            Propose("¿Rodamos hoy?"), GiveUp(),       // seg-006 att#2 item
            Propose("Dónde film mañana?"),            // seg-006 att#3 item (recovers, loop stops)
            Propose("Gracias hoy"), GiveUp(),         // seg-007 item
        ]);
        var meaning = new ScriptedMeaning
        {
            MeaningJsons = new Queue<string>([MeaningFail("drops 'where'"), MeaningPass(), MeaningFail("drops the reason")]),
        };

        RecoveryReport report = await Run(glimmer, meaning.ToHandler(), [seg006Att2, seg006Att3, seg007]);

        // Per attempt: 3 items, 2 recovered (both seg-006 items counted individually? No — only att#3 recovered).
        Assert.Equal(3, report.Totals.TotalItems);
        Assert.Equal(1, report.Totals.Recovered);          // only seg-006 att#3 recovered
        Assert.Equal(2, report.Totals.StillRefused);

        // Per distinct line: 2 lines; seg-006 recovered (one of its attempts did), seg-007 did not.
        Assert.Equal(2, report.Totals.DistinctLines);
        Assert.Equal(1, report.Totals.DistinctLinesRecovered);
        Assert.Equal(1, report.Totals.DistinctLinesStillRefused);

        // The two rates are reported from the same run and need not be equal — that is the point.
        Assert.Equal(report.Totals.TotalItems, report.Totals.Recovered + report.Totals.StillRefused);
        Assert.Equal(report.Totals.DistinctLines, report.Totals.DistinctLinesRecovered + report.Totals.DistinctLinesStillRefused);
    }

    private static IDisposable FakeGlimmerRoute() => new EnvScope(new Dictionary<string, string>
    {
        [GlimmerAuth.BaseUrlEnv] = "https://glimmer.example/openai/v1",
        [GlimmerAuth.ApiKeyEnv] = "glimmer-test-key",
        [GlimmerAuth.ModelEnv] = "muse-glimmer-30b",
    });

    [Fact]
    public async Task Probe_makes_exactly_one_glimmer_call_and_reports_host_models_usage()
    {
        using var _ = FakeGlimmerRoute();
        int calls = 0;
        var handler = new DelegatingHandlerStub((request, _) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Contains("/chat/completions", request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(ChatResponse("{\"ok\": true}", "muse-glimmer-30b")); // schema-constrained readiness reply from a Glimmer model
        });
        using var http = new HttpClient(handler);
        using var client = new GlimmerClient(http);
        var budget = new CallBudget(45);

        GlimmerProbeResult probe = await client.WithBudget(budget).ProbeAsync();

        Assert.Equal(1, calls);                 // exactly one Glimmer call
        Assert.Equal(1, budget.Used);           // charged toward the run cap
        Assert.Equal("glimmer.example", probe.Host);
        Assert.Equal("muse-glimmer-30b", probe.RequestedModel); // from the route
        Assert.Equal("muse-glimmer-30b", probe.ReturnedModel);  // the model the endpoint reported
        Assert.NotNull(probe.Usage);
        Assert.True(probe.WallSeconds >= 0);
    }

    [Fact]
    public async Task Probe_rejects_reply_that_is_not_ok_true()
    {
        using var _ = FakeGlimmerRoute();
        var handler = new DelegatingHandlerStub((_, _) =>
            Task.FromResult(ChatResponse("{\"ok\": false}")));
        using var http = new HttpClient(handler);
        using var client = new GlimmerClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.ProbeAsync());
        Assert.Contains("ok", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_rejects_non_glimmer_returned_model()
    {
        // Route model is non-Glimmer AND returned model is non-Glimmer -> stop.
        using var _ = new EnvScope(new Dictionary<string, string>
        {
            [GlimmerAuth.BaseUrlEnv] = "https://glimmer.example/openai/v1",
            [GlimmerAuth.ApiKeyEnv] = "k",
            [GlimmerAuth.ModelEnv] = "muse-spark-1.1", // not a Glimmer model
        });
        // ChatResponse reports model "muse-spark-1.1" (also not Glimmer).
        var handler = new DelegatingHandlerStub((_, _) => Task.FromResult(ChatResponse("{\"ok\": true}")));
        using var http = new HttpClient(handler);
        using var client = new GlimmerClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.ProbeAsync());
        Assert.Contains("non-Glimmer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_rejects_a_served_model_that_is_not_the_requested_route_model()
    {
        using var _ = FakeGlimmerRoute(); // route model muse-glimmer-30b
        // A different Glimmer variant than the one the route requested means the deployment
        // is not the one under test.
        var handler = new DelegatingHandlerStub((_, _) => Task.FromResult(ChatResponse("{\"ok\": true}", "muse-glimmer-7b")));
        using var http = new HttpClient(handler);
        using var client = new GlimmerClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.ProbeAsync());
        Assert.Contains("served model", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_rejects_a_reply_wrapped_in_prose()
    {
        using var _ = FakeGlimmerRoute();
        // The probe contract is STRICT JSON {"ok": true}; surrounding prose fails the probe.
        var handler = new DelegatingHandlerStub((_, _) =>
            Task.FromResult(ChatResponse("I cannot follow strict JSON, but {\"ok\": true}", "muse-glimmer-30b")));
        using var http = new HttpClient(handler);
        using var client = new GlimmerClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.ProbeAsync());
        Assert.Contains("ok", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_failure_throws_so_the_caller_can_stop_before_the_loop()
    {
        using var _ = FakeGlimmerRoute();
        var handler = new DelegatingHandlerStub((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"invalid key\"}", Encoding.UTF8, "application/json"),
            }));
        using var http = new HttpClient(handler);
        using var client = new GlimmerClient(http);

        MetaApiException ex = await Assert.ThrowsAsync<MetaApiException>(() => client.ProbeAsync());
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_fails_closed_with_zero_calls_when_route_unset()
    {
        using var _ = new EnvScope(new Dictionary<string, string>
        {
            [GlimmerAuth.BaseUrlEnv] = string.Empty,
            [GlimmerAuth.ApiKeyEnv] = string.Empty,
            [GlimmerAuth.ModelEnv] = string.Empty,
        });
        int calls = 0;
        var handler = new DelegatingHandlerStub((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var http = new HttpClient(handler);
        using var client = new GlimmerClient(http);

        await Assert.ThrowsAsync<GlimmerNotConfiguredException>(() => client.ProbeAsync());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Missing_env_configuration_fails_closed_with_zero_calls()
    {
        // No GLIMMER_* variables set: the real client must refuse before any network work.
        using var _ = new EnvScope(new Dictionary<string, string>
        {
            // Deliberately leave base url empty to force the unconfigured path.
            [GlimmerAuth.ApiKeyEnv] = string.Empty,
            [GlimmerAuth.ModelEnv] = string.Empty,
            [GlimmerAuth.BaseUrlEnv] = string.Empty,
        });

        Assert.False(GlimmerAuth.IsConfigured());

        int handlerCalls = 0;
        var handler = new DelegatingHandlerStub((_, _) =>
        {
            Interlocked.Increment(ref handlerCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var http = new HttpClient(handler);
        using var client = new GlimmerClient(http);

        GlimmerNotConfiguredException ex = await Assert.ThrowsAsync<GlimmerNotConfiguredException>(
            () => client.ProposeAsync(new GlimmerRecoveryTurn(MeaningItem(), [], 1)));

        Assert.Equal(0, handlerCalls); // zero network calls
        Assert.Contains(GlimmerAuth.BaseUrlEnv, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-fake-key", ex.Message, StringComparison.Ordinal); // key never echoed
    }

    [Fact]
    public async Task Cap_enforcement_stops_the_run()
    {
        using var _ = FakeMetaAuth();
        // Two items, but a Glimmer budget of 1 call: the first item spends it, the second never calls.
        var glimmer = new FakeGlimmerClient([GiveUp()]); // only one reply will ever be dequeued
        var meaning = new ScriptedMeaning();
        var glimmerBudget = new CallBudget(1);
        var metaBudget = new CallBudget(35);

        RecoveryItem itemA = MeaningItem() with { SegmentId = "seg-a" };
        RecoveryItem itemB = MeaningItem() with { SegmentId = "seg-b" };

        // The service owns charging the Glimmer budget, so the plain fake needs no wrapper.
        using var http = new HttpClient(meaning.ToHandler());
        using var meta = new MetaClient(http);
        var service = new RecoveryAgentService(glimmer, meta, "en", "es", 0.10);
        RecoveryReport report = await service.RunAsync([itemA, itemB], glimmerBudget, metaBudget);

        Assert.Equal(2, report.Items.Length);
        Assert.Equal(1, glimmer.Calls); // only the first item ever called Glimmer
        Assert.Equal(RecoveryOutcome.GaveUp, report.Items[0].Outcome);
        Assert.Equal(RecoveryOutcome.BudgetExhausted, report.Items[1].Outcome);
        Assert.Empty(report.Items[1].Attempts);
    }

    [Fact]
    public async Task A_mid_loop_provider_failure_is_recorded_as_a_run_error_with_completed_items_preserved()
    {
        using var _ = FakeMetaAuth();
        var glimmer = new FailingGlimmer(Propose("Dónde film mañana?"));
        var meaning = new ScriptedMeaning { MeaningJsons = new Queue<string>([MeaningPass()]) };

        RecoveryItem itemA = MeaningItem() with { SegmentId = "seg-a" };
        RecoveryItem itemB = MeaningItem() with { SegmentId = "seg-b" };

        RecoveryReport report = await Run(glimmer, meaning.ToHandler(), [itemA, itemB]);

        Assert.NotNull(report.RunError);
        Assert.Contains("503", report.RunError, StringComparison.Ordinal);
        Assert.Single(report.Items); // the completed first item is preserved
        Assert.Equal(RecoveryOutcome.Recovered, report.Items[0].Outcome);
        Assert.Equal(2, glimmer.Calls); // item B's first proposal failed mid-call
    }

    /// <summary>Glimmer fake that succeeds once, then throws a transport-style provider error.</summary>
    private sealed class FailingGlimmer : IGlimmerClient
    {
        private readonly GlimmerReply _first;

        public FailingGlimmer(GlimmerReply first) => _first = first;

        public int Calls { get; private set; }

        public Task<GlimmerReply> ProposeAsync(GlimmerRecoveryTurn turn, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
            {
                return Task.FromResult(_first);
            }

            throw new MetaApiException("Glimmer HTTP 503: upstream unavailable");
        }
    }

    [Fact]
    public async Task Network_failure_does_not_consume_a_budget_slot()
    {
        using var _ = FakeGlimmerRoute();
        var handler = new DelegatingHandlerStub((_, _) => throw new HttpRequestException("connection refused"));
        using var http = new HttpClient(handler);
        using var client = new GlimmerClient(http);
        var budget = new CallBudget(45);

        await Assert.ThrowsAsync<MetaApiException>(() => client.WithBudget(budget).ProbeAsync());

        Assert.Equal(0, budget.Used); // transient network failure never reached the endpoint -> not charged
    }

    [Fact]
    public async Task Run_stops_requesting_proposals_when_the_meta_budget_is_spent()
    {
        using var _ = FakeMetaAuth();
        // Item A spends the single Meta slot on its meaning check and recovers; item B must
        // NOT spend another Glimmer call just to discover the Meta budget is gone.
        var glimmer = new FakeGlimmerClient([Propose("Dónde film mañana?")]);
        var meaning = new ScriptedMeaning { MeaningJsons = new Queue<string>([MeaningPass()]) };
        var glimmerBudget = new CallBudget(45);
        var metaBudget = new CallBudget(1);

        RecoveryItem itemA = MeaningItem() with { SegmentId = "seg-a" };
        RecoveryItem itemB = MeaningItem() with { SegmentId = "seg-b" };

        RecoveryReport report = await Run(glimmer, meaning.ToHandler(), [itemA, itemB], glimmerBudget, metaBudget);

        Assert.Equal(1, glimmer.Calls); // item B never requested a proposal
        Assert.Equal(1, meaning.Calls);
        Assert.Equal(RecoveryOutcome.Recovered, report.Items[0].Outcome);
        Assert.Equal(RecoveryOutcome.BudgetExhausted, report.Items[1].Outcome);
        Assert.Empty(report.Items[1].Attempts);
    }

    [Fact]
    public async Task Run_totals_and_report_include_the_probe_when_supplied()
    {
        using var _ = FakeMetaAuth();
        var glimmer = new FakeGlimmerClient([GiveUp()]);
        var meaning = new ScriptedMeaning();
        var probe = new GlimmerProbeResult("glimmer.example", "muse-glimmer-30b", "muse-glimmer-30b", new(15, 5, 20), 0.5);

        RecoveryReport report = await Run(glimmer, meaning.ToHandler(), [MeaningItem()], probe: probe);

        // The probe's one call and tokens belong in the run totals: 1 probe + 1 loop call.
        Assert.Equal(2, report.Totals.GlimmerCalls);
        Assert.Equal(GlimmerUsage.TotalTokens + 20, report.Totals.GlimmerTokens.TotalTokens);
        Assert.Equal((0, 0), (report.Totals.MetaCalls, report.Totals.MetaTokens.TotalTokens));
        Assert.NotNull(report.Probe);
        Assert.Equal("glimmer.example", report.Probe!.Host);
        Assert.Equal((15, 5, 20), (report.Probe.PromptTokens, report.Probe.CompletionTokens, report.Probe.TotalTokens));
        Assert.Equal(0.5, report.Probe.WallSeconds);
        Assert.Null(report.RunError);
    }

    [Fact]
    public async Task Prompt_uses_the_effective_run_options_even_when_the_item_carries_different_values()
    {
        using var _ = FakeMetaAuth();
        // The item was captured as es / 10% tolerance, but the service runs fr / 25%: the
        // prompt must advertise exactly what the gates will judge with, so Glimmer is not
        // asked for Spanish and then judged as French.
        var glimmer = new FakeGlimmerClient([GiveUp()]);
        var meaning = new ScriptedMeaning();
        using var http = new HttpClient(meaning.ToHandler());
        using var meta = new MetaClient(http);
        var service = new RecoveryAgentService(glimmer, meta, "en", "fr", 0.25);

        await service.RunAsync([MeaningItem()], null, null);

        Assert.Single(glimmer.Turns);
        string prompt = GlimmerPrompt.BuildUserPrompt(glimmer.Turns[0]);
        Assert.Contains("en->fr", prompt, StringComparison.Ordinal);
        Assert.Contains("tolerance 25%", prompt, StringComparison.Ordinal);
    }
}
