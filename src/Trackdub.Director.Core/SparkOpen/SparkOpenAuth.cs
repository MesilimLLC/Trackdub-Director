using System.Net;

namespace Trackdub.Director.Core.SparkOpen;

/// <summary>
/// Resolves the Muse Spark Open route from environment variables only, mirroring
/// <see cref="MetaAuth"/>'s env-only contract. Spark Open is an Azure Foundry managed
/// compute deployment exposing an OpenAI-compatible chat/completions endpoint that
/// Michael provisions and tears down himself; this process only ever reads the
/// endpoint values he passes into its environment. The API key is read on demand and
/// never logged, printed, stored, or committed.
/// </summary>
public static class SparkOpenAuth
{
    public const string BaseUrlEnv = "SPARKOPEN_BASE_URL";
    public const string ApiKeyEnv = "SPARKOPEN_API_KEY";
    public const string ModelEnv = "SPARKOPEN_MODEL";

    /// <summary>
    /// True when all three required env vars are present and non-blank. Provider selection
    /// and the runners use this to fail closed with zero calls when Spark Open is not configured.
    /// </summary>
    public static bool IsConfigured()
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BaseUrlEnv))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyEnv))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ModelEnv));
    }

    /// <summary>Resolves and validates the full Spark Open configuration, or throws when any part is missing.</summary>
    public static SparkOpenConfig Resolve()
    {
        string baseUrl = Require(BaseUrlEnv).TrimEnd('/');
        string apiKey = Require(ApiKeyEnv);
        string model = Require(ModelEnv);

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
        {
            throw new SparkOpenConfigException($"{BaseUrlEnv} must be an absolute URL (the deployment's OpenAI-compatible chat completions endpoint).");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            // /chat/completions is appended to the base URL; a query/fragment would swallow that suffix.
            throw new SparkOpenConfigException($"{BaseUrlEnv} must be an absolute URL without a query or fragment (the deployment's OpenAI-compatible chat completions endpoint).");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !IsLoopback(uri))
        {
            // Plaintext HTTP would carry the deployment's Bearer key and source text unencrypted; only
            // loopback endpoints may use it (local testing against a forwarded deployment).
            throw new SparkOpenConfigException($"{BaseUrlEnv} must be an https:// URL; only loopback (localhost/127.0.0.1) endpoints may use plaintext http.");
        }

        return new SparkOpenConfig(baseUrl, apiKey, model, uri.Host);
    }

    private static bool IsLoopback(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out IPAddress? address) && IPAddress.IsLoopback(address);
    }

    private static string Require(string env)
    {
        string? raw = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new SparkOpenConfigException(
                $"Spark Open is not configured: set {BaseUrlEnv}, {ApiKeyEnv}, and {ModelEnv} in the environment. The key is never committed to this repo.");
        }

        return raw.Trim();
    }
}

/// <summary>
/// Resolved Spark Open route. <see cref="ApiKey"/> is held only for the lifetime of a request
/// and is never serialized into any report. <see cref="EndpointHost"/> is the host portion of
/// the base URL, safe to record as provenance.
/// </summary>
public sealed record SparkOpenConfig(string BaseUrl, string ApiKey, string Model, string EndpointHost);

/// <summary>Raised when the Spark Open route is incompletely or invalidly configured. Fails closed before any call.</summary>
public sealed class SparkOpenConfigException : SparkOpenException
{
    public SparkOpenConfigException(string message)
        : base(message)
    {
    }
}

/// <summary>Base type for Spark Open lane failures. Distinct from <see cref="MetaApiException"/> so a Spark Open failure is never mistaken for a hosted one.</summary>
public class SparkOpenException : Exception
{
    public SparkOpenException(string message)
        : base(message)
    {
    }

    public SparkOpenException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
