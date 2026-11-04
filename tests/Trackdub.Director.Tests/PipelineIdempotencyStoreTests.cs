using Microsoft.AspNetCore.Http;

namespace Trackdub.Director.Tests;

public sealed class PipelineIdempotencyStoreTests
{
    [Fact]
    public async Task Replays_same_key_and_payload_without_executing_paid_stages_again()
    {
        var store = new PipelineIdempotencyStore();
        string key = ServerTestSupport.NewIdempotencyKey();
        var completion = new TaskCompletionSource<IResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        Task<IResult> first = store.RunAsync(key, "fingerprint", () =>
        {
            calls++;
            return completion.Task;
        });
        Task<IResult> retry = store.RunAsync(key, "fingerprint", () =>
        {
            calls++;
            return Task.FromResult(Results.Text("unexpected duplicate"));
        });

        var result = Results.Text("cached pipeline result");
        completion.SetResult(result);
        Assert.Same(result, await first);
        Assert.Same(result, await retry);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Rejects_reuse_of_a_key_for_a_different_request()
    {
        var store = new PipelineIdempotencyStore();
        string key = ServerTestSupport.NewIdempotencyKey();
        int calls = 0;
        _ = await store.RunAsync(key, "first", () =>
        {
            calls++;
            return Task.FromResult(Results.Text("done"));
        });

        IResult conflict = await store.RunAsync(key, "different", () =>
        {
            calls++;
            return Task.FromResult(Results.Text("must not execute"));
        });

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(conflict).StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Restarted_store_rejects_seen_key_without_reexecuting_request()
    {
        string directory = Path.Combine(Path.GetTempPath(), "trackdub-idempotency-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "ledger.json");
        string key = ServerTestSupport.NewIdempotencyKey();
        int calls = 0;

        try
        {
            var firstProcess = new PipelineIdempotencyStore(path);
            _ = await firstProcess.RunAsync(key, "fingerprint", () =>
            {
                calls++;
                return Task.FromResult<IResult>(Results.Text("completed"));
            });

            var restartedProcess = new PipelineIdempotencyStore(path);
            IResult retry = await restartedProcess.RunAsync(key, "fingerprint", () =>
            {
                calls++;
                return Task.FromResult<IResult>(Results.Text("duplicate"));
            });

            Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(retry).StatusCode);
            Assert.Equal(1, calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Rejects_old_key_without_executing_request()
    {
        var store = new PipelineIdempotencyStore();
        string key = $"{(DateTimeOffset.UtcNow - PipelineIdempotencyStore.Retention - TimeSpan.FromMinutes(1)).ToUnixTimeMilliseconds()}_{Guid.NewGuid():D}";
        int calls = 0;

        IResult expired = await store.RunAsync(key, "fingerprint", () =>
        {
            calls++;
            return Task.FromResult<IResult>(Results.Text("must not execute"));
        });

        Assert.Equal(StatusCodes.Status410Gone, Assert.IsAssignableFrom<IStatusCodeHttpResult>(expired).StatusCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Accepts_scoped_audition_key()
    {
        var store = new PipelineIdempotencyStore();
        string key = $"audition:{ServerTestSupport.NewIdempotencyKey()}";
        int calls = 0;

        IResult result = await store.RunAsync(key, "fingerprint", () =>
        {
            calls++;
            return Task.FromResult<IResult>(Results.Json(new { ok = true }, statusCode: StatusCodes.Status200OK));
        });

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(1, calls);
    }
}
