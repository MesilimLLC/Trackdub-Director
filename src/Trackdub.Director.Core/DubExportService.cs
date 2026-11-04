using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Trackdub.Director.Core;

/// <summary>
/// Renders the basic dubbed MP4: source video picture, original audio omitted,
/// and gate-winning WAV takes placed at explicit segment starts. ffmpeg and
/// ffprobe are external tools found on PATH unless FFMPEG_PATH / FFPROBE_PATH
/// point elsewhere. Validation fails before encoding when any expected winner
/// or start is missing, overlaps another winner, or runs past the video.
/// </summary>
public sealed class FfmpegDubExportService : IDubExportService
{
    public const long MaxVideoBytes = 200L * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim ProcessSlots = new(2, 2);

    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".webm", ".mkv",
    };

    private readonly string _ffmpeg;
    private readonly string _ffprobe;

    public FfmpegDubExportService()
        : this(
            Environment.GetEnvironmentVariable("FFMPEG_PATH"),
            Environment.GetEnvironmentVariable("FFPROBE_PATH"))
    {
    }

    public FfmpegDubExportService(string? ffmpegPath, string? ffprobePath)
    {
        _ffmpeg = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();
        _ffprobe = string.IsNullOrWhiteSpace(ffprobePath) ? "ffprobe" : ffprobePath.Trim();
    }

    public async Task<Stream> RenderAsync(DubExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string extension = ValidateRequest(request);
        string directory = CreateTempDirectory();
        try
        {
            DubExportRenderFiles files = await PrepareFilesAsync(request, extension, directory, cancellationToken).ConfigureAwait(false);
            DubExportPlan plan = DubExportPlanner.Build(request.ExpectedSegmentIds, request.Segments, files.VideoDurationSeconds);
            await EncodeAsync(files, plan, cancellationToken).ConfigureAwait(false);
            await VerifyOutputAsync(files, cancellationToken).ConfigureAwait(false);
            return new DeleteOnDisposeFileStream(files.OutputPath, directory);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    private static string ValidateRequest(DubExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Video);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VideoFileName);
        string extension = Path.GetExtension(request.VideoFileName);
        return SupportedVideoExtensions.Contains(extension)
            ? extension.ToLowerInvariant()
            : throw new ArgumentException("Original video must be MP4, MOV, M4V, WebM, or MKV.", nameof(request));
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "trackdub-dub-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private async Task<DubExportRenderFiles> PrepareFilesAsync(
        DubExportRequest request,
        string extension,
        string directory,
        CancellationToken cancellationToken)
    {
        string videoPath = Path.Combine(directory, "source" + extension);
        await CopyBoundedAsync(request.Video, videoPath, MaxVideoBytes, cancellationToken).ConfigureAwait(false);
        double duration = await ProbeDurationAsync(videoPath, cancellationToken).ConfigureAwait(false);
        DubExportPlan plan = DubExportPlanner.Build(request.ExpectedSegmentIds, request.Segments, duration);
        string[] audioPaths = await WriteAudioFilesAsync(plan, directory, cancellationToken).ConfigureAwait(false);
        return new DubExportRenderFiles(videoPath, audioPaths, Path.Combine(directory, "trackdub-dub.mp4"), duration);
    }

    private static async Task<string[]> WriteAudioFilesAsync(
        DubExportPlan plan,
        string directory,
        CancellationToken cancellationToken)
    {
        var paths = new string[plan.Placements.Count];
        for (int i = 0; i < plan.Placements.Count; i++)
        {
            paths[i] = Path.Combine(directory, $"take-{i:000}.wav");
            await File.WriteAllBytesAsync(paths[i], plan.Placements[i].WavAudio, cancellationToken).ConfigureAwait(false);
        }
        return paths;
    }

    private async Task EncodeAsync(DubExportRenderFiles files, DubExportPlan plan, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunProcessAsync(
            _ffmpeg, BuildFfmpegArguments(files, plan), EncodeTimeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !HasOutput(files.OutputPath))
        {
            throw new InvalidOperationException($"ffmpeg could not render the dubbed MP4: {Truncate(result.StandardError)}");
        }
    }

    private async Task VerifyOutputAsync(DubExportRenderFiles files, CancellationToken cancellationToken)
    {
        double outputDuration = await ProbeDurationAsync(files.OutputPath, cancellationToken).ConfigureAwait(false);
        if (Math.Abs(outputDuration - files.VideoDurationSeconds) > 0.75)
        {
            throw new InvalidOperationException(
                $"Rendered MP4 duration {FormatSeconds(outputDuration)}s does not match the {FormatSeconds(files.VideoDurationSeconds)}s source video.");
        }
    }

    private static bool HasOutput(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    internal static string[] BuildFfmpegArguments(DubExportRenderFiles files, DubExportPlan plan)
    {
        var args = new List<string> { "-v", "error", "-y", "-i", files.VideoPath };
        foreach (string audioPath in files.AudioPaths)
        {
            args.Add("-i");
            args.Add(audioPath);
        }

        args.AddRange([
            "-filter_complex", DubExportPlanner.BuildAudioFilter(plan, files.VideoDurationSeconds),
            "-map", "0:v:0",
            "-map", "[aout]",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "18",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", "192k",
            "-ar", "48000",
            "-movflags", "+faststart",
            "-t", FormatSeconds(files.VideoDurationSeconds),
            files.OutputPath,
        ]);
        return args.ToArray();
    }

    private async Task<double> ProbeDurationAsync(string path, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunProcessAsync(
            _ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=duration:format=duration", "-of", "json", path],
            ProbeTimeout,
            cancellationToken).ConfigureAwait(false);
        return TryParseDuration(result, out double duration)
            ? duration
            : throw new InvalidOperationException($"ffprobe could not read a positive video-stream duration from the source video: {Truncate(result.StandardError)}");
    }

    private static bool TryParseDuration(ProcessResult result, out double duration)
    {
        duration = 0;
        if (result.ExitCode != 0) return false;
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;
            return TryReadDuration(root, "streams", out duration) || TryReadDuration(root, "format", out duration);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadDuration(JsonElement root, string property, out double duration)
    {
        duration = 0;
        if (!root.TryGetProperty(property, out JsonElement element)) return false;
        if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() == 0) return false;
            element = element[0];
        }
        return element.TryGetProperty("duration", out JsonElement value)
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration)
            && double.IsFinite(duration)
            && duration > 0;
    }

    private static async Task CopyBoundedAsync(Stream source, string destination, long maxBytes, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        long total = await CopyCountingAsync(source, output, maxBytes, cancellationToken).ConfigureAwait(false);
        if (total == 0) throw new ArgumentException("Original video is empty.", nameof(source));
    }

    private static async Task<long> CopyCountingAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new ArgumentException($"Original video exceeds the {maxBytes} byte export limit.", nameof(source));
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return total;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string exe,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await ProcessSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            CancellationToken processToken = timeoutSource.Token;
            using var process = StartProcess(exe, args);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(processToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(processToken);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(processToken), stdout, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
            {
                KillProcess(process);
                await DrainQuietlyAsync(stdout, stderr).ConfigureAwait(false);
                throw new InvalidOperationException($"'{exe}' timed out after {timeout.TotalSeconds:0} seconds.");
            }
            catch (OperationCanceledException)
            {
                KillProcess(process);
                await DrainQuietlyAsync(stdout, stderr).ConfigureAwait(false);
                throw;
            }
            return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        finally
        {
            ProcessSlots.Release();
        }
    }

    private static Process StartProcess(string exe, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);

        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start '{exe}'.");
            return process;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException($"Could not start '{exe}'. Install ffmpeg/ffprobe or set FFMPEG_PATH and FFPROBE_PATH.", ex);
        }
    }

    private static void KillProcess(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    private static async Task DrainQuietlyAsync(Task<string> stdout, Task<string> stderr)
    {
        try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static string FormatSeconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Truncate(string value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "no diagnostic output" : value.Trim();
        return text.Length <= 1000 ? text : text[..1000];
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class DeleteOnDisposeFileStream : FileStream
    {
        private readonly string _directory;
        private int _disposed;

        public DeleteOnDisposeFileStream(string path, string directory)
            : base(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
        {
            _directory = directory;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Interlocked.Exchange(ref _disposed, 1) == 0) TryDeleteDirectory(_directory);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _disposed, 1) == 0) TryDeleteDirectory(_directory);
        }
    }
}
