namespace Trackdub.Director.Core.SparkOpen;

/// <summary>
/// The Spark Open provider surface the lane's runners drive. A real implementation
/// (<see cref="SparkOpenClient"/>) talks to the Azure deployment through an OpenAI-compatible
/// chat/completions endpoint; a fake implementation (<see cref="FakeSparkOpenClient"/>) scripts
/// replies for offline tests so Phase 1 makes zero live calls.
///
/// <para><see cref="JudgeMeaningAsync"/> is the independent-judge path: Spark Open judges a source
/// line against a Spanish candidate with the same meaning-v1 criteria the hosted
/// <see cref="MeaningGate"/> uses, returning a verdict plus a one-line reason. A reply whose final
/// answer cannot be extracted is a failed judgement (<see cref="MeaningVerdict.Error"/>), carrying
/// its usage, never a guessed verdict.</para>
///
/// <para><see cref="AsMetaClient"/> hands back a <see cref="MetaClient"/> whose chat/completions
/// requests are transparently routed to Spark Open, so the existing QA loop and existing meaning
/// gate run unchanged with Spark Open as the provider.</para>
/// </summary>
public interface ISparkOpenClient : IDisposable
{
    /// <summary>The deployment/model name Spark Open reports, for provenance in reports.</summary>
    string Model { get; }

    /// <summary>The endpoint host, safe to record as provenance. Never includes the key.</summary>
    string EndpointHost { get; }

    /// <summary>
    /// Judge one candidate for meaning fidelity with the meaning-v1 criteria. The returned
    /// <see cref="MeaningCheckResult"/> carries Pass/Fail (or Error on an unextractable/failed reply),
    /// a one-line reason, and usage. Never throws for a model-level failure — it records Error instead.
    /// </summary>
    Task<MeaningCheckResult> JudgeMeaningAsync(
        JudgeMeaningRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A <see cref="MetaClient"/> bound to Spark Open's chat/completions endpoint, for driving the
    /// existing <see cref="QaService"/> and <see cref="TranslationService"/> with Spark Open as the
    /// translation/revision provider. The returned client shares this client's lifetime.
    /// </summary>
    MetaClient AsMetaClient();
}

/// <summary>The source line, candidate translation, and language pair to judge.</summary>
public sealed record JudgeMeaningRequest(
    string SourceText,
    string Candidate,
    string SourceLanguage,
    string TargetLanguage);
