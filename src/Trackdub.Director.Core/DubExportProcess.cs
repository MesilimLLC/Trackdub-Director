using System.Diagnostics;

namespace Trackdub.Director.Core;

internal sealed record DubExportProcessRequest(string Executable, IReadOnlyList<string> Arguments, TimeSpan Timeout);
internal sealed record DubExportProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class DubExportProcess
{
    private static readonly SemaphoreSlim ProcessSlots = new(2, 2);

    internal static async Task<DubExportProcessResult> RunAsync(
        DubExportProcessRequest request,
        CancellationToken cancellationToken)
    {
        await ProcessSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(request.Timeout);
            CancellationToken processToken = timeoutSource.Token;
            using var process = StartProcess(request);
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
                throw new InvalidOperationException($"'{request.Executable}' timed out after {request.Timeout.TotalSeconds:0} seconds.");
            }
            catch (OperationCanceledException)
            {
                KillProcess(process);
                await DrainQuietlyAsync(stdout, stderr).ConfigureAwait(false);
                throw;
            }
            return new DubExportProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        finally
        {
            ProcessSlots.Release();
        }
    }

    private static Process StartProcess(DubExportProcessRequest request)
    {
        var start = new ProcessStartInfo(request.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in request.Arguments) start.ArgumentList.Add(arg);

        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start '{request.Executable}'.");
            return process;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException($"Could not start '{request.Executable}'. Install ffmpeg/ffprobe or set FFMPEG_PATH and FFPROBE_PATH.", ex);
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

}
