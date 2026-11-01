using Trackdub.Director.Core;

// Writes every synthesized audition take and the manifest when AUDITION_OUTPUT_DIR is set.
internal static class AuditionOutputs
{
    // Persists the takes + the tracked manifest only when AUDITION_OUTPUT_DIR
    // names the dataset folder (live capture runs). API tests leave it unset so
    // no fake-run files ever land in the repo.
    // One writer at a time, so two runs cannot interleave their take files and manifest.
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    internal static async Task TryWriteAuditionOutputsAsync(AuditionReport report, CancellationToken ct)
    {
        string? outputDir = Environment.GetEnvironmentVariable("AUDITION_OUTPUT_DIR");
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return;
        }

        await WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AuditionOutputWriter.WriteAsync(report, outputDir, ct).ConfigureAwait(false);
        }
        finally
        {
            WriteLock.Release();
        }
    }
}
