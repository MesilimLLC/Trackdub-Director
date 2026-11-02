namespace Trackdub.Director.Core;

/// <summary>
/// Resolves the Muse-Glimmer route from environment variables only, mirroring the
/// <see cref="MetaAuth"/> contract: the API key is never read from a file and never
/// logged. Phase 1 fails closed — <see cref="GetRoute"/> throws a
/// <see cref="GlimmerNotConfiguredException"/> when any of the three route variables
/// is unset, and NO call is ever made without a complete route.
/// </summary>
public static class GlimmerAuth
{
    public const string BaseUrlEnv = "GLIMMER_BASE_URL";
    public const string ApiKeyEnv = "GLIMMER_API_KEY";
    public const string ModelEnv = "GLIMMER_MODEL";

    /// <summary>
    /// Optional reasoning control for the Glimmer route. The agent uses a low
    /// selectable reasoning effort by default; this variable overrides it. Accepted
    /// values match the OpenAI-compatible reasoning_effort field: low, medium, high.
    /// </summary>
    public const string ReasoningEffortEnv = "GLIMMER_REASONING_EFFORT";

    public const string DefaultReasoningEffort = "low";

    /// <summary>
    /// A fully-resolved Glimmer route. Only constructed when all three required
    /// variables are present; the key is held in memory for the duration of a call
    /// and never written anywhere.
    /// </summary>
    public sealed record GlimmerRoute(string BaseUrl, string ApiKey, string Model, string? ReasoningEffort);

    /// <summary>
    /// True when all three required route variables are set. A cheap, key-value-free
    /// check the CLI and service use to fail closed before constructing any client.
    /// </summary>
    public static bool IsConfigured()
    {
        return HasValue(BaseUrlEnv) && HasValue(ApiKeyEnv) && HasValue(ModelEnv);
    }

    /// <summary>
    /// Resolve the complete route, or throw <see cref="GlimmerNotConfiguredException"/>
    /// naming the missing variables. Fail-closed: a partially-configured route is never
    /// returned, so no call can be built from one.
    /// </summary>
    public static GlimmerRoute GetRoute()
    {
        var missing = new List<string>(3);
        if (!HasValue(BaseUrlEnv))
        {
            missing.Add(BaseUrlEnv);
        }

        if (!HasValue(ApiKeyEnv))
        {
            missing.Add(ApiKeyEnv);
        }

        if (!HasValue(ModelEnv))
        {
            missing.Add(ModelEnv);
        }

        if (missing.Count > 0)
        {
            throw new GlimmerNotConfiguredException(missing);
        }

        return new GlimmerRoute(
            GetTrimmed(BaseUrlEnv)!.TrimEnd('/'),
            GetTrimmed(ApiKeyEnv)!,
            GetTrimmed(ModelEnv)!,
            GetReasoningEffort());
    }

    public static string? GetReasoningEffort()
    {
        string? raw = Environment.GetEnvironmentVariable(ReasoningEffortEnv);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultReasoningEffort;
        }

        return raw.Trim().ToLowerInvariant();
    }

    private static bool HasValue(string env) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(env));

    private static string? GetTrimmed(string env)
    {
        string? raw = Environment.GetEnvironmentVariable(env);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }
}

/// <summary>
/// Raised when a Glimmer call is attempted without a complete route. The message names
/// the missing variables but NEVER echoes any value, so a key is never exposed even in
/// an error path.
/// </summary>
public sealed class GlimmerNotConfiguredException : MetaApiException
{
    public GlimmerNotConfiguredException(IReadOnlyList<string> missing)
        : base($"Glimmer route is not configured; no call was made. Set {string.Join(", ", missing)} in the environment (never committed).")
    {
        Missing = missing;
    }

    public IReadOnlyList<string> Missing { get; }
}
