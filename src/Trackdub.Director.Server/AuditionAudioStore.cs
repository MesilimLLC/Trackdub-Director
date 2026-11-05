using Trackdub.Director.Core;

// Keeps synthesized takes in bounded temporary storage for range-capable playback.
// IDs are random and paths are never derived from caller-provided filenames.
internal sealed class AuditionAudioStore : IDisposable
{
    internal const long MaxRetainedBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private readonly object _gate = new();
    private readonly string _root;
    private readonly long _maxRetainedBytes;
    private readonly Dictionary<string, AudioEntry> _files = new(StringComparer.Ordinal);
    private long _storedBytes;

    internal AuditionAudioStore(string? root = null, long maxRetainedBytes = MaxRetainedBytes)
    {
        _root = root ?? Path.Combine(Path.GetTempPath(), "trackdub-director-audio", Guid.NewGuid().ToString("N"));
        _maxRetainedBytes = maxRetainedBytes;
        Directory.CreateDirectory(_root);
    }

    internal AuditionReport Enrich(AuditionReport report)
    {
        var locations = new Dictionary<AuditionTakeReport, string?>(ReferenceEqualityComparer.Instance);
        var protectedIds = new HashSet<string>(StringComparer.Ordinal);
        var candidates = report.Segments
            .SelectMany(segment => segment.Takes)
            .Where(take => take.AudioBytes is { Length: > 0 })
            .OrderByDescending(take => take.IsWinner)
            .ToArray();

        foreach (AuditionTakeReport take in candidates)
        {
            locations[take] = Store(take.AudioBytes!, protectedIds);
        }

        AuditionSegmentReport[] segments = report.Segments.Select(segment => segment with
        {
            Takes = segment.Takes.Select(take =>
            {
                if (!locations.TryGetValue(take, out string? id))
                {
                    return take with { AudioBytes = null, AudioDataUrl = null, AudioUrl = null };
                }

                return take with
                {
                    AudioBytes = null,
                    AudioDataUrl = null,
                    AudioUrl = id is null ? null : $"/api/audition-audio/{id}",
                    AudioTransportOmitted = id is null ? "audio_storage_limit" : null,
                };
            }).ToArray(),
        }).ToArray();

        return report with { Segments = segments };
    }

    internal string? ResolvePath(string id)
    {
        lock (_gate)
        {
            PruneExpired();
            return Guid.TryParseExact(id, "N", out _)
                && _files.TryGetValue(id, out AudioEntry? entry)
                && File.Exists(entry.Path)
                    ? entry.Path
                    : null;
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temporary audio is private to this process. Disposal attempts to
            // delete it; a process that exits without disposal leaves the root
            // behind, and nothing prunes it later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string? Store(byte[] audio, HashSet<string> protectedIds)
    {
        lock (_gate)
        {
            PruneExpired();
            if (audio.LongLength > _maxRetainedBytes)
            {
                return null;
            }

            foreach (AudioEntry oldest in _files.Values
                .Where(entry => !protectedIds.Contains(entry.Id))
                .OrderBy(entry => entry.CreatedAt)
                .ToArray())
            {
                if (_storedBytes + audio.LongLength <= _maxRetainedBytes)
                {
                    break;
                }

                Delete(oldest.Id);
            }

            if (_storedBytes + audio.LongLength > _maxRetainedBytes)
            {
                return null;
            }

            string id = Guid.NewGuid().ToString("N");
            string path = Path.Combine(_root, id + ".wav");
            try
            {
                File.WriteAllBytes(path, audio);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            var entry = new AudioEntry(id, path, audio.LongLength, DateTimeOffset.UtcNow);
            _files.Add(id, entry);
            protectedIds.Add(id);
            _storedBytes += entry.Length;
            return id;
        }
    }

    private void PruneExpired()
    {
        DateTimeOffset expiredBefore = DateTimeOffset.UtcNow - Retention;
        foreach (string id in _files.Values
            .Where(entry => entry.CreatedAt < expiredBefore)
            .Select(entry => entry.Id)
            .ToArray())
        {
            Delete(id);
        }
    }

    private void Delete(string id)
    {
        if (!_files.Remove(id, out AudioEntry? entry))
        {
            return;
        }

        _storedBytes -= entry.Length;
        try
        {
            File.Delete(entry.Path);
        }
        catch (IOException)
        {
        }
    }

    private sealed record AudioEntry(string Id, string Path, long Length, DateTimeOffset CreatedAt);
}
