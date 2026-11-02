namespace Trackdub.Director.Core.SparkOpen;

/// <summary>
/// Which translation/meaning provider a Spark Open lane run uses. The hosted Meta Model API
/// (<see cref="MetaClient"/>, muse-spark-1.1) is the default everywhere; Spark Open is opt-in
/// per run and never a silent fallback.
/// </summary>
public enum TextProvider
{
    /// <summary>The hosted Meta Model API path. The default for every lane.</summary>
    Hosted,

    /// <summary>The Azure Foundry Muse Spark Open deployment, opt-in per run.</summary>
    SparkOpen,
}

/// <summary>
/// Resolves the provider for a run. Selection is always explicit: the default is
/// <see cref="TextProvider.Hosted"/>, and Spark Open is chosen only when a caller asks for it
/// by name. A Spark Open failure is never retried against the hosted model under the same label.
/// </summary>
public static class ProviderSelection
{
    /// <summary>The provider used when a run does not opt in to anything: always hosted.</summary>
    public static TextProvider Default => TextProvider.Hosted;

    /// <summary>
    /// Maps an opt-in flag to a provider. <paramref name="useSparkOpen"/> defaults to false,
    /// so the absence of an explicit choice always selects the hosted path.
    /// </summary>
    public static TextProvider Select(bool useSparkOpen = false)
        => useSparkOpen ? TextProvider.SparkOpen : TextProvider.Hosted;

    /// <summary>
    /// Parses a provider name case-insensitively. Null, empty, or "hosted" select the hosted
    /// default; "spark-open" / "sparkopen" select Spark Open. Any other value is an input error,
    /// so a typo can never silently fall through to a provider the caller did not intend.
    /// </summary>
    public static TextProvider Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), "hosted", StringComparison.OrdinalIgnoreCase))
        {
            return TextProvider.Hosted;
        }

        string normalized = name.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        return string.Equals(normalized, "sparkopen", StringComparison.OrdinalIgnoreCase)
            ? TextProvider.SparkOpen
            : throw new SparkOpenConfigException($"Unknown provider '{name}'. Use 'hosted' (default) or 'spark-open'.");
    }
}
