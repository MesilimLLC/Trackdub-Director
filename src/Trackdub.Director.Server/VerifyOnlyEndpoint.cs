using System.Text.Json;
using Trackdub.Director.Core;

// POST /api/verify-only: runs the zero-call gate over a captured run already on disk. No upload path:
// the request names a run folder, and it must resolve inside the captured-runs root.
internal static class VerifyOnlyEndpoint
{
    internal const string RunsRootSetting = "VerifyOnly:RunsRoot";

    internal static async Task<IResult> HandleAsync(HttpRequest request, IConfiguration configuration, IWebHostEnvironment environment, CancellationToken ct)
    {
        var (run, readError) = await ApiHandlers.ReadOrBadRequestAsync(() => ReadRunAsync(request, ct));
        if (readError is not null)
        {
            return readError;
        }

        if (string.IsNullOrWhiteSpace(run))
        {
            return Results.BadRequest(new { error = "Request body must be a JSON object with a 'run' folder name, e.g. { \"run\": \"voa10\" }." });
        }

        string root = ResolveRunsRoot(configuration, environment);
        string? runDir = TryResolveRunDir(root, run);
        if (runDir is null)
        {
            return Results.BadRequest(new { error = "'run' must name a folder inside the captured-runs root." });
        }

        if (!Directory.Exists(runDir))
        {
            return Results.NotFound(new { error = $"No captured run named '{run}'." });
        }

        if (ContainsLink(root, runDir))
        {
            return Results.BadRequest(new { error = "'run' must not be, or pass through, a symbolic link." });
        }

        try
        {
            VerifyReceipt receipt = VerifyOnlyService.Verify(runDir);
            return Results.Text(receipt.ToJson(), "application/json");
        }
        catch (Exception ex) when (ex is VerifyInputException or IOException or UnauthorizedAccessException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<string?> ReadRunAsync(HttpRequest request, CancellationToken ct)
    {
        using JsonDocument doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("run", out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    // Configured root wins; otherwise the nearest samples folder at or above the content root, then the working directory.
    internal static string ResolveRunsRoot(IConfiguration configuration, IWebHostEnvironment environment)
    {
        string? configured = configuration[RunsRootSetting];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        for (DirectoryInfo? dir = new(environment.ContentRootPath); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "samples");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.GetFullPath("samples");
    }

    // Null for any run name that is not a plain relative folder name inside the root, including names
    // that make path resolution throw (such as an embedded NUL).
    private static string? TryResolveRunDir(string root, string run)
    {
        if (Path.IsPathRooted(run) || run.IndexOfAny(['\\', ':']) >= 0)
        {
            return null;
        }

        try
        {
            string runDir = Path.GetFullPath(Path.Combine(root, run));
            return IsInside(root, runDir) ? runDir : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // Any link between the root and the run folder could point outside the root, so none is followed.
    private static bool ContainsLink(string root, string runDir)
    {
        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (string? dir = Path.TrimEndingDirectorySeparator(runDir); dir is not null && dir.Length > rootFull.Length; dir = Path.GetDirectoryName(dir))
        {
            if (new DirectoryInfo(dir).LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInside(string root, string path)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }
}
