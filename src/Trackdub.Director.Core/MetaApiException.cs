namespace Trackdub.Director.Core;

public class MetaApiException : Exception
{
    public MetaApiException(string message)
        : base(message)
    {
    }

    public MetaApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MetaApiException(string message, Exception innerException, MetaClient.TokenUsage? usage)
        : base(message, innerException)
    {
        Usage = usage;
    }

    public MetaApiException(string message, MetaClient.TokenUsage? usage)
        : base(message)
    {
        Usage = usage;
    }

    /// <summary>
    /// Token usage the API billed for a call that still failed. The hosted error body (and the
    /// Spark Open handler's 502 body) carries usage for failed calls; preserving it here lets
    /// callers account for billed tokens on the failure path instead of recording zero.
    /// </summary>
    public MetaClient.TokenUsage? Usage { get; }
}
