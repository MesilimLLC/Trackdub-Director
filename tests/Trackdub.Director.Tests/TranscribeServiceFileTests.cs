using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Tests for <see cref="TranscribeService"/>'s file paths: reading a WAV from disk
/// and writing the segments / full result JSON. Uses a fake ASR client, so no network.
/// </summary>
public sealed class TranscribeServiceFileTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "trackdub-transcribe-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FakeClient : IMuseTranscribeClient
    {
        public string? LastFileName { get; private set; }

        public long LastStreamLength { get; private set; }

        public Task<TranscribeRawResponse> TranscribeAsync(
            Stream wavAudio,
            string? fileName = null,
            CancellationToken cancellationToken = default)
        {
            LastFileName = fileName;
            LastStreamLength = wavAudio.Length;
            return Task.FromResult(new TranscribeRawResponse(
                SessionId: "session-1",
                Transcript: "hola mundo",
                AudioDurationMs: 1500,
                Turns:
                [
                    new TranscribeTurn(TurnId: 1, StartMs: 0, EndMs: 1000, Transcript: "  hola  ", Speaker: "A"),
                    new TranscribeTurn(TurnId: 2, StartMs: 1000, EndMs: 1500, Transcript: "   ", Speaker: "B"),
                ]));
        }
    }

    private string WriteWav(byte[] bytes, string name = "input.wav")
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TranscribeFileAsync_rejects_a_blank_path(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new TranscribeService(new FakeClient()).TranscribeFileAsync(path));
    }

    [Fact]
    public async Task TranscribeFileAsync_fails_when_the_file_is_missing()
    {
        var service = new TranscribeService(new FakeClient());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TranscribeFileAsync(Path.Combine(_folder, "missing.wav")));
    }

    [Fact]
    public async Task TranscribeFileAsync_sends_the_file_name_and_maps_the_turns()
    {
        byte[] bytes = [0x52, 0x49, 0x46, 0x46, 0x01, 0x02];
        string path = WriteWav(bytes);
        var client = new FakeClient();

        TranscribeResult result = await new TranscribeService(client).TranscribeFileAsync(path);

        Assert.Equal("input.wav", client.LastFileName);
        Assert.Equal(bytes.Length, client.LastStreamLength);

        // The blank turn is skipped and ids stay contiguous; duration is endMs-startMs.
        TranscribeSegment segment = Assert.Single(result.Segments);
        Assert.Equal("seg-001", segment.Id);
        Assert.Equal("hola", segment.Text);
        Assert.Equal(1.0, segment.DurationSeconds);
        Assert.Equal("A", segment.Speaker);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task WriteSegmentsAsync_creates_the_folder_and_writes_the_segment_array()
    {
        var service = new TranscribeService(new FakeClient());
        TranscribeResult result = await service.TranscribeFileAsync(WriteWav([0x01]));
        string path = Path.Combine(_folder, "nested", "segments.json");

        await service.WriteSegmentsAsync(path, result);

        using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal("seg-001", doc.RootElement[0].GetProperty("id").GetString());
        Assert.Equal("hola", doc.RootElement[0].GetProperty("text").GetString());
        Assert.Equal("A", doc.RootElement[0].GetProperty("speaker").GetString());
    }

    [Fact]
    public async Task WriteSegmentsAsync_rejects_a_blank_path_and_a_null_result()
    {
        var service = new TranscribeService(new FakeClient());
        TranscribeResult result = await service.TranscribeFileAsync(WriteWav([0x01]));

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.WriteSegmentsAsync("  ", result));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.WriteSegmentsAsync(Path.Combine(_folder, "segments.json"), null!));
    }

    [Fact]
    public async Task WriteResultAsync_writes_the_full_result_with_its_wire_names()
    {
        var service = new TranscribeService(new FakeClient());
        TranscribeResult result = await service.TranscribeFileAsync(WriteWav([0x01]));
        string path = Path.Combine(_folder, "nested", "result.json");

        await service.WriteResultAsync(path, result);

        string json = await File.ReadAllTextAsync(path);
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.Equal("session-1", doc.RootElement.GetProperty("session_id").GetString());
        Assert.Equal(1500, doc.RootElement.GetProperty("audio_duration_ms").GetInt64());
        Assert.True(doc.RootElement.TryGetProperty("endpoint_host", out _));
        // Usage is never reported by the ASR endpoint, and that must be explicit.
        Assert.False(doc.RootElement.GetProperty("usage_reported").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("segments").GetArrayLength());
    }

    [Fact]
    public async Task WriteResultAsync_rejects_a_blank_path_and_a_null_result()
    {
        var service = new TranscribeService(new FakeClient());
        TranscribeResult result = await service.TranscribeFileAsync(WriteWav([0x01]));

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.WriteResultAsync("  ", result));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.WriteResultAsync(Path.Combine(_folder, "result.json"), null!));
    }
}
