using System.Text;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class AuditionTransportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "trackdub-audition-transport-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static AuditionTakeReport Take(int number, byte[] bytes, bool winner) =>
        new(
            TakeNumber: number,
            Kind: QaAttemptKind.Initial,
            Text: "Hola.",
            MeaningVerdict: MeaningVerdict.Pass,
            MeaningIssues: null,
            MeaningExplanation: null,
            Synthesized: true,
            MeasuredSeconds: 1,
            WindowSeconds: 1,
            TimingVerdict: AuditionTimingVerdict.Pass,
            IsWinner: winner,
            AudioFileName: $"seg-001-take{number}.wav",
            SynthesisNote: null,
            TranslationTokens: 0,
            MeaningTokens: 0,
            ElevenLabsCharacters: 5,
            AudioBytes: bytes);

    private static AuditionReport Report(params AuditionTakeReport[] takes) =>
        new(
            SourceLanguage: "en",
            TargetLanguage: "es",
            Cast: [new AuditionCastVoice("anna", "voice-1", "Voice One")],
            Segments:
            [
                new AuditionSegmentReport(
                    Id: "seg-001",
                    Text: "Hello.",
                    SpeakerId: "anna",
                    CastVoice: null,
                    WindowSeconds: 1,
                    Takes: takes,
                    Cast: true,
                    WinnerTakeNumber: takes.FirstOrDefault(t => t.IsWinner)?.TakeNumber,
                    WinnerFileName: takes.FirstOrDefault(t => t.IsWinner)?.AudioFileName),
            ],
            Totals: new AuditionTotals(1, 1, takes.Length, 1, 0, 0, 5, false, false));

    [Fact]
    public void Enrich_prioritizes_winner_and_bounds_aggregate_audio()
    {
        byte[] huge = new byte[AuditionTransport.MaxAudioTransportBytes];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(huge, 0);
        byte[] winnerBytes = Encoding.ASCII.GetBytes("RIFFwinner");
        AuditionReport enriched = AuditionTransport.Enrich(Report(
            Take(1, huge, winner: false),
            Take(2, winnerBytes, winner: true)));

        AuditionTakeReport loser = enriched.Segments[0].Takes[0];
        AuditionTakeReport winner = enriched.Segments[0].Takes[1];
        Assert.Null(loser.AudioDataUrl);
        Assert.Equal("response_audio_limit", loser.AudioTransportOmitted);
        Assert.NotNull(winner.AudioDataUrl);
        Assert.StartsWith("data:audio/wav;base64,", winner.AudioDataUrl);
        Assert.Equal(winnerBytes, Convert.FromBase64String(winner.AudioDataUrl.Split(',', 2)[1]));
    }

    [Fact]
    public async Task Manifest_strips_browser_audio_transport_fields()
    {
        AuditionReport enriched = AuditionTransport.Enrich(Report(
            Take(1, Encoding.ASCII.GetBytes("RIFFtransport"), winner: true)));

        string manifestPath = await AuditionOutputWriter.WriteAsync(enriched, _folder);
        string manifest = await File.ReadAllTextAsync(manifestPath);

        Assert.DoesNotContain("audio_data_url", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("audio_transport_omitted", manifest, StringComparison.Ordinal);
        Assert.Contains("audio_sha256", manifest, StringComparison.Ordinal);
    }
}
