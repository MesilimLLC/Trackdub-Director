using System.Security.Cryptography;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Tests for <see cref="AuditionOutputs"/>: the env-gated wrapper that persists audition
/// takes and the manifest only when <c>AUDITION_OUTPUT_DIR</c> names a folder.
/// </summary>
public sealed class AuditionOutputsTests : IDisposable
{
    private const string EnvVar = "AUDITION_OUTPUT_DIR";

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "trackdub-audition-" + Guid.NewGuid().ToString("N"));

    private readonly string? _previous = Environment.GetEnvironmentVariable(EnvVar);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, _previous);
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static readonly byte[] TakeBytes = [0x52, 0x49, 0x46, 0x46, 0x00, 0x01, 0x02, 0x03];

    private static AuditionTakeReport Take(bool withBytes, string fileName = "seg-001-take1.wav") =>
        new(
            TakeNumber: 1,
            Kind: QaAttemptKind.Initial,
            Text: "hola mundo",
            MeaningVerdict: MeaningVerdict.Pass,
            MeaningIssues: null,
            MeaningExplanation: null,
            Synthesized: withBytes,
            MeasuredSeconds: 2.0,
            WindowSeconds: 2.5,
            TimingVerdict: AuditionTimingVerdict.Pass,
            IsWinner: true,
            AudioFileName: fileName,
            SynthesisNote: null,
            TranslationTokens: 0,
            MeaningTokens: 0,
            ElevenLabsCharacters: 10,
            AudioBytes: withBytes ? TakeBytes : null);

    private static AuditionReport Report(params AuditionTakeReport[] takes) =>
        new(
            SourceLanguage: "es",
            TargetLanguage: "en",
            Cast: [new AuditionCastVoice(SpeakerId: "speaker-1", VoiceId: "voice-1", VoiceName: "Voice One")],
            Segments:
            [
                new AuditionSegmentReport(
                    Id: "seg-001",
                    Text: "hola mundo",
                    SpeakerId: "speaker-1",
                    CastVoice: null,
                    WindowSeconds: 2.5,
                    Takes: takes,
                    Cast: true,
                    WinnerTakeNumber: 1,
                    WinnerFileName: null),
            ],
            Totals: new AuditionTotals(
                TotalSegments: 1,
                LinesCast: 1,
                TakesSynthesized: takes.Count(t => t.Synthesized),
                MetaCalls: 1,
                TranslationTokens: 0,
                MeaningTokens: 0,
                ElevenLabsCharacters: 10,
                ElevenLabsCapHit: false,
                MetaCapHit: false));

    [Fact]
    public async Task TryWriteAuditionOutputsAsync_does_nothing_when_the_env_var_is_unset()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);

        await AuditionOutputs.TryWriteAuditionOutputsAsync(Report(Take(withBytes: true)), CancellationToken.None);

        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task TryWriteAuditionOutputsAsync_does_nothing_when_the_env_var_is_blank()
    {
        Environment.SetEnvironmentVariable(EnvVar, "   ");

        await AuditionOutputs.TryWriteAuditionOutputsAsync(Report(Take(withBytes: true)), CancellationToken.None);

        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task TryWriteAuditionOutputsAsync_writes_the_take_and_the_manifest_when_set()
    {
        Environment.SetEnvironmentVariable(EnvVar, _folder);

        await AuditionOutputs.TryWriteAuditionOutputsAsync(Report(Take(withBytes: true)), CancellationToken.None);

        string takePath = Path.Combine(_folder, "media", "audition", "seg-001-take1.wav");
        string manifestPath = Path.Combine(_folder, AuditionOutputWriter.ManifestFileName);

        Assert.True(File.Exists(takePath), "The take file should be written under media/audition.");
        Assert.Equal(TakeBytes, await File.ReadAllBytesAsync(takePath));
        Assert.True(File.Exists(manifestPath), "The manifest should be written at the output root.");

        using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        JsonElement take = doc.RootElement.GetProperty("segments")[0].GetProperty("takes")[0];

        Assert.Equal("seg-001-take1.wav", take.GetProperty("audio_file_name").GetString());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(TakeBytes)).ToLowerInvariant(),
            take.GetProperty("audio_sha256").GetString());
    }

    [Fact]
    public async Task TryWriteAuditionOutputsAsync_omits_a_take_that_was_never_synthesized()
    {
        Environment.SetEnvironmentVariable(EnvVar, _folder);

        await AuditionOutputs.TryWriteAuditionOutputsAsync(Report(Take(withBytes: false)), CancellationToken.None);

        string takePath = Path.Combine(_folder, "media", "audition", "seg-001-take1.wav");
        Assert.False(File.Exists(takePath), "A take without audio bytes must not produce a file.");

        using JsonDocument doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_folder, AuditionOutputWriter.ManifestFileName)));
        JsonElement take = doc.RootElement.GetProperty("segments")[0].GetProperty("takes")[0];

        // The manifest must never name a file that was not written.
        Assert.Equal(JsonValueKind.Null, take.GetProperty("audio_file_name").ValueKind);
    }

    [Fact]
    public async Task TryWriteAuditionOutputsAsync_rejects_two_takes_with_the_same_file_name()
    {
        Environment.SetEnvironmentVariable(EnvVar, _folder);

        var report = Report(
            Take(withBytes: true, fileName: "seg-001-take1.wav"),
            Take(withBytes: true, fileName: "seg-001-take1.wav"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => AuditionOutputs.TryWriteAuditionOutputsAsync(report, CancellationToken.None));
    }
}
