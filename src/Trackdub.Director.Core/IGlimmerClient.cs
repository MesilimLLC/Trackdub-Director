namespace Trackdub.Director.Core;

/// <summary>
/// One recovery turn's prompt to the Glimmer agent: the leakage-safe item plus the
/// trace of attempts so far (so the agent can reason over what it already tried and
/// why each was refused). The harness owns the gates; this is only the model turn.
/// </summary>
public sealed record GlimmerRecoveryTurn(
    RecoveryItem Item,
    IReadOnlyList<RecoveryAttemptTrace> PriorAttempts,
    int AttemptNumber);

/// <summary>
/// The Glimmer recovery-agent seam. The harness calls <see cref="ProposeAsync"/> once per
/// attempt; the implementation performs exactly one model call and returns the parsed
/// reply (or a malformed marker), never judging the candidate itself. A fake
/// implementation lets Phase 1 tests drive the loop with scripted replies and asserted
/// call counts, mirroring <see cref="IMuseImageClient"/>.
/// </summary>
public interface IGlimmerClient
{
    /// <summary>
    /// Ask Glimmer for one action for this turn. Returns a parsed on-schema action, or a
    /// <see cref="GlimmerReply"/> flagged malformed when the reply is off-schema (never
    /// repaired). Implementations must fail closed with zero calls when the route is unset.
    /// </summary>
    Task<GlimmerReply> ProposeAsync(GlimmerRecoveryTurn turn, CancellationToken cancellationToken = default);
}
