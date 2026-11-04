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
        if (separator <= prefixLength
            || separator == key.Length - 1
            || !long.TryParse(key.AsSpan(prefixLength, separator - prefixLength), NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp)
            || !Guid.TryParseExact(key[(separator + 1)..], "D", out _))
        {
            return false;
        }

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

        lock (_gate)
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
            if (_entries.TryGetValue(key, out Entry? existing))
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return Task.FromResult(Results.Conflict(new { error = "This Idempotency-Key was already used for a different request." }));
                }

                return existing.Result ?? UnknownOutcomeResult();
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

            var entry = new Entry(key, fingerprint, createdAt);
            _entries.Add(key, entry);
            if (!PersistEntries())
            {
                _entries.Remove(key);
                return RecoveryUnavailableResult();
            }

            entry.Result = ExecuteSafelyAsync(execute, entry);
            return entry.Result;
        }
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
                    _entries[item.Key] = new Entry(item.Key, item.Fingerprint, item.CreatedAt, item.CompletedAt);
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

    private sealed class Entry(string key, string fingerprint, DateTimeOffset createdAt, DateTimeOffset? completedAt = null)
    {
        public string Key { get; } = key;
        public string Fingerprint { get; } = fingerprint;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset? CompletedAt { get; set; } = completedAt;
        public Task<IResult>? Result { get; set; }
    }

    private sealed record PersistedEntry(string Key, string Fingerprint, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
}
