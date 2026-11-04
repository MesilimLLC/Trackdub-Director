using System.Diagnostics;
using System.Globalization;

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

    public async Task<Stream> RenderAsync(
        Stream video,
        string videoFileName,
        IReadOnlyList<string> expectedSegmentIds,
        IReadOnlyList<DubExportSegmentInput> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentException.ThrowIfNullOrWhiteSpace(videoFileName);
        string extension = Path.GetExtension(videoFileName);
        if (!SupportedVideoExtensions.Contains(extension))
        {
            throw new ArgumentException("Original video must be MP4, MOV, M4V, WebM, or MKV.", nameof(videoFileName));
        }

        string directory = Path.Combine(Path.GetTempPath(), "trackdub-dub-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string videoPath = Path.Combine(directory, "source" + extension.ToLowerInvariant());
            await CopyBoundedAsync(video, videoPath, MaxVideoBytes, cancellationToken).ConfigureAwait(false);
            double videoDuration = await ProbeDurationAsync(videoPath, cancellationToken).ConfigureAwait(false);
            DubExportPlan plan = DubExportPlanner.Build(expectedSegmentIds, segments, videoDuration);

            var audioPaths = new string[plan.Placements.Count];
            for (int i = 0; i < plan.Placements.Count; i++)
            {
                audioPaths[i] = Path.Combine(directory, $"take-{i:000}.wav");
                await File.WriteAllBytesAsync(audioPaths[i], plan.Placements[i].WavAudio, cancellationToken).ConfigureAwait(false);
            }

            string outputPath = Path.Combine(directory, "trackdub-dub.mp4");
            string[] args = BuildFfmpegArguments(videoPath, audioPaths, plan, videoDuration, outputPath);
            ProcessResult result = await RunProcessAsync(_ffmpeg, args, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                throw new InvalidOperationException($"ffmpeg could not render the dubbed MP4: {Truncate(result.StandardError)}");
            }

            double outputDuration = await ProbeDurationAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (Math.Abs(outputDuration - videoDuration) > 0.75)
            {
                throw new InvalidOperationException(
                    $"Rendered MP4 duration {outputDuration:0.###}s does not match the {videoDuration:0.###}s source video.");
            }

            return new DeleteOnDisposeFileStream(outputPath, directory);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    internal static string[] BuildFfmpegArguments(
        string videoPath,
        IReadOnlyList<string> audioPaths,
        DubExportPlan plan,
        double videoDurationSeconds,
        string outputPath)
    {
        var args = new List<string> { "-v", "error", "-y", "-i", videoPath };
        foreach (string audioPath in audioPaths)
        {
            args.Add("-i");
            args.Add(audioPath);
        }

        args.AddRange([
            "-filter_complex", DubExportPlanner.BuildAudioFilter(plan, videoDurationSeconds),
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
            "-t", videoDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            outputPath,
        ]);
        return args.ToArray();
    }

    private async Task<double> ProbeDurationAsync(string path, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunProcessAsync(
            _ffprobe,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !double.TryParse(
                result.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double duration)
            || !double.IsFinite(duration) || duration <= 0)
        {
            throw new InvalidOperationException($"ffprobe could not read a positive duration from the source video: {Truncate(result.StandardError)}");
        }
        return duration;
    }

    private static async Task CopyBoundedAsync(Stream source, string destination, long maxBytes, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new ArgumentException($"Original video exceeds the {maxBytes} byte export limit.", nameof(source));
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (total == 0)
        {
            throw new ArgumentException("Original video is empty.", nameof(source));
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(string exe, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start '{exe}'.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not start '{exe}'. Install ffmpeg/ffprobe or set FFMPEG_PATH and FFPROBE_PATH.", ex);
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

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
