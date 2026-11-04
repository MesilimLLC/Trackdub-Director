using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

// Keeps paid request results briefly so a browser can replay a dropped response
// without paying for the stages again. A small durable key ledger prevents a
// server restart from making an ambiguous request executable a second time.
internal sealed class PipelineIdempotencyStore
{
    internal static readonly TimeSpan Retention = TimeSpan.FromMinutes(20);
    private const int MaxEntries = 128;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly string? _persistencePath;
    private bool _persistenceHealthy = true;

    internal PipelineIdempotencyStore(string? persistencePath = null)
    {
        _persistencePath = persistencePath;
        LoadPersistedEntries();
    }

    internal static bool TryParseKey(string? key, out DateTimeOffset createdAt)
    {
        createdAt = default;
        if (string.IsNullOrWhiteSpace(key)) return false;

        int prefixLength = key.LastIndexOf(':') + 1;
        int separator = key.IndexOf('_', prefixLength);
        if (separator <= prefixLength) return false;
        if (separator == key.Length - 1) return false;
        if (!long.TryParse(key.AsSpan(prefixLength, separator - prefixLength), NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp)) return false;
        if (!Guid.TryParseExact(key[(separator + 1)..], "D", out _)) return false;

        try
        {
            createdAt = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    internal Task<IResult> RunAsync(string key, string fingerprint, Func<Task<IResult>> execute)
    {
        if (!TryParseKey(key, out DateTimeOffset createdAt))
        {
            return Task.FromResult(Results.BadRequest(new { error = "Idempotency-Key must be a timestamped UUID." }));
        }

        var request = new IdempotencyRequest(key, fingerprint);
        lock (_gate)
        {
            return RunLocked(request, createdAt, execute);
        }
    }

    private Task<IResult> RunLocked(IdempotencyRequest request, DateTimeOffset createdAt, Func<Task<IResult>> execute)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (createdAt > now.AddMinutes(1))
        {
            return Task.FromResult(Results.BadRequest(new { error = "Idempotency-Key timestamp is in the future." }));
        }

        if (createdAt < now - Retention)
        {
            return ExpiredResult();
        }

        PruneExpired(now);
        if (_entries.TryGetValue(request.Key, out Entry? existing))
        {
            return ReplayExisting(existing, request);
        }

        if (!_persistenceHealthy)
        {
            return RecoveryUnavailableResult();
        }

        if (_entries.Count >= MaxEntries)
        {
            return Task.FromResult(Results.Json(
                new { error = "Idempotency recovery capacity is full; retry later rather than starting a duplicate run." },
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        return StartNew(request, createdAt, execute);
    }

    private static Task<IResult> ReplayExisting(Entry existing, IdempotencyRequest request)
    {
        if (!string.Equals(existing.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
        {
            return Task.FromResult(Results.Conflict(new { error = "This Idempotency-Key was already used for a different request." }));
        }

        return existing.Result ?? UnknownOutcomeResult();
    }

    private Task<IResult> StartNew(IdempotencyRequest request, DateTimeOffset createdAt, Func<Task<IResult>> execute)
    {
        var entry = new Entry(request, createdAt);
        _entries.Add(request.Key, entry);
        if (!PersistEntries())
        {
            _entries.Remove(key);
            return RecoveryUnavailableResult();
        }

        entry.Result = ExecuteSafelyAsync(execute, entry);
        return entry.Result;
    }

    private void LoadPersistedEntries()
    {
        if (string.IsNullOrWhiteSpace(_persistencePath) || !File.Exists(_persistencePath)) return;

        try
        {
            List<PersistedEntry>? persisted = JsonSerializer.Deserialize<List<PersistedEntry>>(File.ReadAllText(_persistencePath));
            if (persisted is null) throw new InvalidDataException("Idempotency ledger is empty.");

            DateTimeOffset retainAfter = DateTimeOffset.UtcNow - Retention;
            foreach (PersistedEntry item in persisted)
            {
                if (item.CreatedAt >= retainAfter && TryParseKey(item.Key, out _))
                {
                    _entries[item.Key] = new Entry(new IdempotencyRequest(item.Key, item.Fingerprint), item.CreatedAt, item.CompletedAt);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            // Fail closed: losing the key ledger could turn an ambiguous retry
            // into a second paid request.
            _persistenceHealthy = false;
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        DateTimeOffset expiredBefore = now - Retention;
        string[] expired = _entries.Values
            .Where(entry => entry.CreatedAt < expiredBefore)
            .Select(entry => entry.Key)
            .ToArray();
        if (expired.Length == 0) return;

        foreach (string key in expired) _entries.Remove(key);
        _ = PersistEntries();
    }

    private bool PersistEntries()
    {
        if (string.IsNullOrWhiteSpace(_persistencePath)) return true;

        string? directory = Path.GetDirectoryName(_persistencePath);
        string temporaryPath = _persistencePath + ".tmp";
        try
        {
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            PersistedEntry[] snapshot = _entries.Values
                .Select(entry => new PersistedEntry(entry.Key, entry.Fingerprint, entry.CreatedAt, entry.CompletedAt))
                .ToArray();
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot));
            File.Move(temporaryPath, _persistencePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _persistenceHealthy = false;
            try { File.Delete(temporaryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    private async Task<IResult> ExecuteSafelyAsync(Func<Task<IResult>> execute, Entry entry)
    {
        try
        {
            return await execute().ConfigureAwait(false);
        }
        catch
        {
            return Results.Json(
                new { error = "Request execution ended without a response. Reuse the same Idempotency-Key to check its result." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
        finally
        {
            lock (_gate)
            {
                entry.CompletedAt = DateTimeOffset.UtcNow;
                _ = PersistEntries();
            }
        }
    }

    private static Task<IResult> ExpiredResult() => Task.FromResult(Results.Json(
        new { error = "This Idempotency-Key expired and will not be executed again. Start a new run only if you intend to repeat paid stages." },
        statusCode: StatusCodes.Status410Gone));

    private static Task<IResult> UnknownOutcomeResult() => Task.FromResult(Results.Conflict(new
    {
        error = "This request key was recorded before the server restarted. Its provider outcome is unknown, and it will not be executed again. Review provider history before starting a new run.",
    }));

    private static Task<IResult> RecoveryUnavailableResult() => Task.FromResult(Results.Json(
        new { error = "Idempotency recovery storage is unavailable; the paid request was not started." },
        statusCode: StatusCodes.Status503ServiceUnavailable));

    private sealed class Entry(IdempotencyRequest request, DateTimeOffset createdAt, DateTimeOffset? completedAt = null)
    {
        public string Key { get; } = request.Key;
        public string Fingerprint { get; } = request.Fingerprint;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset? CompletedAt { get; set; } = completedAt;
        public Task<IResult>? Result { get; set; }
    }

    private sealed record PersistedEntry(string Key, string Fingerprint, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

    private readonly record struct IdempotencyRequest(string Key, string Fingerprint);
}
