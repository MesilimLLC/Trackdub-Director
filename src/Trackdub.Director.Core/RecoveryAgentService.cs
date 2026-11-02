namespace Trackdub.Director.Core;

/// <summary>
/// Runs the Glimmer recovery loop over a set of refused items. Glimmer PROPOSES; the
/// existing deterministic gates DISPOSE: each proposal is judged by the SAME production
/// paths real candidates are judged by — timing via <see cref="DurationEstimator"/> and
/// meaning via <see cref="MetaClient.CheckMeaningAsync"/> (template meaning-v1, hosted
/// muse-spark-1.1). A line counts as recovered only when a proposal passes BOTH gates.
/// Glimmer's own opinion of its candidate is never an accept.
///
/// Per item: at most <see cref="MaxAttemptsPerItem"/> attempts. Each attempt the harness
/// (not Glimmer) runs the gates in order — timing first, meaning only if timing passes.
/// A malformed Glimmer reply is a FAILED attempt, recorded as such, never repaired and
/// never counted as a proposal. The item stops at its first full pass (recovered) or at
/// the attempt cap / give_up (still refused). Run-level provider budgets are enforced via
/// <see cref="CallBudget"/>; when either budget is exhausted the item ends BudgetExhausted
/// and no further provider call is attempted.
/// </summary>
public sealed class RecoveryAgentService
{
    public const int MaxAttemptsPerItem = 3;

    private readonly IGlimmerClient _glimmer;
    private readonly MetaClient _meta;
    private readonly string _sourceLanguage;
    private readonly string _targetLanguage;
    private readonly double _toleranceFraction;

    /// <param name="glimmer">The recovery-agent seam. In Phase 1 tests this is the fake client.</param>
    /// <param name="meta">The hosted Meta client used for the meaning gate. In Phase 1 tests its
    /// HttpClient is backed by a fake handler, so the REAL CheckMeaningAsync runs with no live call.</param>
    public RecoveryAgentService(
        IGlimmerClient glimmer,
        MetaClient meta,
        string sourceLanguage = "en",
        string targetLanguage = "es",
        double toleranceFraction = 0.10)
    {
        _glimmer = glimmer ?? throw new ArgumentNullException(nameof(glimmer));
        _meta = meta ?? throw new ArgumentNullException(nameof(meta));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentOutOfRangeException.ThrowIfNegative(toleranceFraction);

        _sourceLanguage = sourceLanguage;
        _targetLanguage = targetLanguage;
        _toleranceFraction = toleranceFraction;
    }

    /// <summary>
    /// Run the loop over every item. <paramref name="glimmerBudget"/> and
    /// <paramref name="metaBudget"/> cap the run's total Glimmer and hosted Meta calls
    /// respectively; pass null to leave a provider uncapped (tests may). The service owns
    /// charging the Glimmer budget (one reserve per proposal), so the cap holds even for a
    /// caller-supplied client that does not charge it. Pass the <paramref name="probe"/>
    /// the CLI performed pre-flight so its one call, its tokens, and its details appear in
    /// the run totals and report.
    /// </summary>
    public async Task<RecoveryReport> RunAsync(
        IReadOnlyList<RecoveryItem> items,
        CallBudget? glimmerBudget = null,
        CallBudget? metaBudget = null,
        GlimmerProbeResult? probe = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        MetaClient meta = metaBudget is null ? _meta : _meta.WithBudget(metaBudget);
        var itemReports = new List<RecoveryItemReport>(items.Count);
        var totals = new RunAccumulator();

        // The pre-flight probe is charged to the same Glimmer budget, so its one call and its
        // tokens belong in the run totals; otherwise every report undercounts Glimmer usage.
        if (probe is not null)
        {
            totals.AddGlimmer(probe.Usage);
        }

        // A Glimmer transport failure (HTTP error, timeout) mid-loop must not discard the items
        // already processed: record the failure on the report so the caller can persist partial work.
        string? runError = null;
        try
        {
            foreach (RecoveryItem item in items)
            {
                RecoveryItemReport report = await ProcessItemAsync(item, meta, glimmerBudget, metaBudget, totals, cancellationToken).ConfigureAwait(false);
                itemReports.Add(report);
            }
        }
        catch (MetaApiException ex)
        {
            runError = ex.Message;
        }

        return BuildReport(items, itemReports, totals, glimmerBudget, metaBudget, probe, runError);
    }

    private async Task<RecoveryItemReport> ProcessItemAsync(
        RecoveryItem item,
        MetaClient meta,
        CallBudget? glimmerBudget,
        CallBudget? metaBudget,
        RunAccumulator totals,
        CancellationToken cancellationToken)
    {
        var attempts = new List<RecoveryAttemptTrace>(MaxAttemptsPerItem);
        RecoveryOutcome outcome = RecoveryOutcome.ExhaustedAttempts;
        string? recoveredText = null;

        for (int attemptNumber = 1; attemptNumber <= MaxAttemptsPerItem; attemptNumber++)
        {
            // Stop before requesting another proposal when EITHER provider budget is spent:
            // without Meta calls no candidate can be meaning-checked, so the proposal is wasted.
            if (BudgetSpent(glimmerBudget) || BudgetSpent(metaBudget))
            {
                outcome = RecoveryOutcome.BudgetExhausted;
                break;
            }

            GlimmerReply reply;
            try
            {
                // The service owns charging the Glimmer budget, so the cap holds regardless of
                // whether the supplied client charges it. The probe is charged by the CLI before
                // the loop; this reserves exactly the loop's own proposals.
                glimmerBudget?.Reserve();

                // Present the run's effective direction and tolerance to Glimmer, so the prompt
                // advertises exactly what the gates judge with — never the report's retained
                // values that the CLI options may have overridden.
                RecoveryItem promptItem = item with
                {
                    SourceLanguage = _sourceLanguage,
                    TargetLanguage = _targetLanguage,
                    ToleranceFraction = _toleranceFraction,
                };
                var turn = new GlimmerRecoveryTurn(promptItem, attempts.ToArray(), attemptNumber);
                reply = await _glimmer.ProposeAsync(turn, cancellationToken).ConfigureAwait(false);
            }
            catch (MetaBudgetExhaustedException)
            {
                outcome = RecoveryOutcome.BudgetExhausted;
                break;
            }

            totals.AddGlimmer(reply.Usage);

            AttemptEvaluation evaluation = await EvaluateReplyAsync(item, reply, meta, totals, attemptNumber, cancellationToken).ConfigureAwait(false);
            attempts.Add(evaluation.Trace);

            if (evaluation.Trace.AttemptRecovered)
            {
                outcome = RecoveryOutcome.Recovered;
                recoveredText = evaluation.Trace.ProposedText;
                break;
            }

            if (evaluation.GaveUp)
            {
                outcome = RecoveryOutcome.GaveUp;
                break;
            }

            if (evaluation.BudgetExhausted)
            {
                outcome = RecoveryOutcome.BudgetExhausted;
                break;
            }
        }

        return new RecoveryItemReport(
            item.SourceFile, item.SegmentId, item.AttemptNumber, item.SourceLine, item.WindowSeconds,
            item.FailureType, outcome, recoveredText, attempts.ToArray());
    }

    /// <summary>
    /// Judge one Glimmer reply through the production gates. A malformed reply or a give_up
    /// consumes the attempt without a proposal. A proposal is judged timing-first (real
    /// <see cref="DurationEstimator"/>), and meaning only if timing passes (real
    /// <see cref="MetaClient.CheckMeaningAsync"/>). Recovered iff timing Pass AND meaning Pass.
    /// </summary>
    private async Task<AttemptEvaluation> EvaluateReplyAsync(
        RecoveryItem item,
        GlimmerReply reply,
        MetaClient meta,
        RunAccumulator totals,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        if (reply.Malformed || reply.Action is null)
        {
            return AttemptEvaluation.NonProposal(new RecoveryAttemptTrace(
                attemptNumber, GlimmerAction: null, MalformedReply: true, ProposedText: null,
                Strategy: null, MeasuredSeconds: null, item.WindowSeconds,
                TimingVerdict: QaVerdict.Fail, MeaningVerdict: MeaningVerdict.NotEvaluated,
                MeaningIssues: null, MeaningExplanation: null, AttemptRecovered: false,
                GlimmerUsage: GlimmerUsageRecord.From(reply.Usage), MeaningUsage: GlimmerUsageRecord.Zero));
        }

        GlimmerAction action = reply.Action;
        if (action.Action == GlimmerActionKind.GiveUp)
        {
            return AttemptEvaluation.GiveUp(new RecoveryAttemptTrace(
                attemptNumber, GlimmerActionKind.GiveUp, MalformedReply: false, ProposedText: null,
                action.Strategy, MeasuredSeconds: null, item.WindowSeconds,
                TimingVerdict: QaVerdict.Fail, MeaningVerdict: MeaningVerdict.NotEvaluated,
                MeaningIssues: null, MeaningExplanation: null, AttemptRecovered: false,
                GlimmerUsage: GlimmerUsageRecord.From(reply.Usage), MeaningUsage: GlimmerUsageRecord.Zero));
        }

        string candidate = action.TextEs!;
        double measured = DurationEstimator.EstimateSeconds(candidate, item.TargetCharactersPerSecond);
        bool timingFits = DurationEstimator.Fits(measured, item.WindowSeconds, _toleranceFraction);

        if (!timingFits)
        {
            // Meaning is deliberately NOT evaluated when timing fails — exactly as production.
            return AttemptEvaluation.Refused(new RecoveryAttemptTrace(
                attemptNumber, GlimmerActionKind.Propose, MalformedReply: false, candidate,
                action.Strategy, measured, item.WindowSeconds,
                TimingVerdict: QaVerdict.Fail, MeaningVerdict: MeaningVerdict.NotEvaluated,
                MeaningIssues: null, MeaningExplanation: null, AttemptRecovered: false,
                GlimmerUsage: GlimmerUsageRecord.From(reply.Usage), MeaningUsage: GlimmerUsageRecord.Zero));
        }

        MeaningCheckResult meaning;
        try
        {
            meaning = await meta.CheckMeaningAsync(
                new MetaClient.MeaningCheckRequest(item.SourceLine, candidate, _sourceLanguage, _targetLanguage),
                cancellationToken).ConfigureAwait(false);
        }
        catch (MetaBudgetExhaustedException)
        {
            // Budget spent before the meaning check could run: the attempt is unjudged.
            return AttemptEvaluation.BudgetStopped(new RecoveryAttemptTrace(
                attemptNumber, GlimmerActionKind.Propose, MalformedReply: false, candidate,
                action.Strategy, measured, item.WindowSeconds,
                TimingVerdict: QaVerdict.Pass, MeaningVerdict: MeaningVerdict.NotEvaluated,
                MeaningIssues: null, MeaningExplanation: null, AttemptRecovered: false,
                GlimmerUsage: GlimmerUsageRecord.From(reply.Usage), MeaningUsage: GlimmerUsageRecord.Zero));
        }
        catch (Exception ex) when (
            ex is MetaApiException or HttpRequestException or InvalidOperationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            meaning = MeaningGate.ForError(ex.Message, MetaAuth.GetModel(), null, GetEndpointHost());
        }

        totals.AddMeta(meaning.Usage);
        bool recovered = meaning.Verdict == MeaningVerdict.Pass;

        return AttemptEvaluation.Refused(new RecoveryAttemptTrace(
            attemptNumber, GlimmerActionKind.Propose, MalformedReply: false, candidate,
            action.Strategy, measured, item.WindowSeconds,
            TimingVerdict: QaVerdict.Pass, MeaningVerdict: meaning.Verdict,
            MeaningIssues: meaning.Issues, MeaningExplanation: meaning.Explanation, AttemptRecovered: recovered,
            GlimmerUsage: GlimmerUsageRecord.From(reply.Usage), MeaningUsage: GlimmerUsageRecord.From(meaning.Usage)));
    }

    private RecoveryReport BuildReport(
        IReadOnlyList<RecoveryItem> items,
        IReadOnlyList<RecoveryItemReport> itemReports,
        RunAccumulator totals,
        CallBudget? glimmerBudget,
        CallBudget? metaBudget,
        GlimmerProbeResult? probe,
        string? runError)
    {
        int recovered = itemReports.Count(r => r.Outcome == RecoveryOutcome.Recovered);

        // Distinct-line rollup: key by (source_file, segment_id). A line is recovered if
        // ANY of its attempt-items recovered. Reported alongside the per-attempt counts so
        // the rate cannot be accused of double-counting a line refused on two attempts.
        var lineGroups = itemReports
            .GroupBy(r => (r.SourceFile, r.SegmentId))
            .ToArray();
        int distinctLines = lineGroups.Length;
        int distinctLinesRecovered = lineGroups.Count(g => g.Any(r => r.Outcome == RecoveryOutcome.Recovered));

        var totalsOut = new RecoveryTotals(
            TotalItems: itemReports.Count,
            Recovered: recovered,
            StillRefused: itemReports.Count - recovered,
            DistinctLines: distinctLines,
            DistinctLinesRecovered: distinctLinesRecovered,
            DistinctLinesStillRefused: distinctLines - distinctLinesRecovered,
            GlimmerCalls: totals.GlimmerCalls,
            MetaCalls: totals.MetaCalls,
            GlimmerCallCap: glimmerBudget?.Limit ?? 0,
            MetaCallCap: metaBudget?.Limit ?? 0,
            GlimmerTokens: totals.GlimmerTokens(),
            MetaTokens: totals.MetaTokens());

        string reasoning = GlimmerAuth.IsConfigured()
            ? GlimmerAuth.GetReasoningEffort() ?? GlimmerAuth.DefaultReasoningEffort
            : GlimmerAuth.DefaultReasoningEffort;
        string glimmerModel = GlimmerAuth.IsConfigured() ? GlimmerAuth.GetRoute().Model : "(unconfigured)";

        return new RecoveryReport(
            SourceLanguage: _sourceLanguage,
            TargetLanguage: _targetLanguage,
            GlimmerModel: glimmerModel,
            GlimmerReasoningEffort: reasoning,
            MeaningModel: MetaAuth.GetModel(),
            MeaningPromptTemplateVersion: MeaningGate.PromptTemplateVersion,
            MaxAttemptsPerItem: MaxAttemptsPerItem,
            Probe: probe is null
                ? null
                : new RecoveryProbeInfo(
                    probe.Host,
                    probe.RequestedModel,
                    probe.ReturnedModel,
                    probe.Usage?.PromptTokens ?? 0,
                    probe.Usage?.CompletionTokens ?? 0,
                    probe.Usage?.TotalTokens ?? 0,
                    probe.WallSeconds),
            RunError: runError,
            Items: itemReports.ToArray(),
            Totals: totalsOut);
    }

    private static bool BudgetSpent(CallBudget? budget) => budget is not null && budget.Remaining <= 0;

    private static string GetEndpointHost()
    {
        string baseUrl = MetaAuth.GetBaseUrl();
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ? uri.Host : baseUrl;
    }

    private sealed record AttemptEvaluation(RecoveryAttemptTrace Trace, bool GaveUp, bool BudgetExhausted)
    {
        public static AttemptEvaluation Refused(RecoveryAttemptTrace trace) => new(trace, false, false);

        public static AttemptEvaluation NonProposal(RecoveryAttemptTrace trace) => new(trace, false, false);

        public static AttemptEvaluation GiveUp(RecoveryAttemptTrace trace) => new(trace, true, false);

        public static AttemptEvaluation BudgetStopped(RecoveryAttemptTrace trace) => new(trace, false, true);
    }

    private sealed class RunAccumulator
    {
        private int _glimmerPrompt;
        private int _glimmerCompletion;
        private int _glimmerTotal;
        private int _metaPrompt;
        private int _metaCompletion;
        private int _metaTotal;

        public int GlimmerCalls { get; private set; }

        public int MetaCalls { get; private set; }

        public void AddGlimmer(MetaClient.TokenUsage? usage)
        {
            GlimmerCalls++;
            _glimmerPrompt += usage?.PromptTokens ?? 0;
            _glimmerCompletion += usage?.CompletionTokens ?? 0;
            _glimmerTotal += usage?.TotalTokens ?? ((usage?.PromptTokens ?? 0) + (usage?.CompletionTokens ?? 0));
        }

        public void AddMeta(MetaClient.TokenUsage? usage)
        {
            MetaCalls++;
            _metaPrompt += usage?.PromptTokens ?? 0;
            _metaCompletion += usage?.CompletionTokens ?? 0;
            _metaTotal += usage?.TotalTokens ?? ((usage?.PromptTokens ?? 0) + (usage?.CompletionTokens ?? 0));
        }

        public GlimmerUsageRecord GlimmerTokens() => new(_glimmerPrompt, _glimmerCompletion, _glimmerTotal);

        public GlimmerUsageRecord MetaTokens() => new(_metaPrompt, _metaCompletion, _metaTotal);
    }
}
