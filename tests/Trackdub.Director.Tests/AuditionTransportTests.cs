using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Trackdub.Director.Core;
using static Trackdub.Director.Tests.ServerTestSupport;

namespace Trackdub.Director.Tests;

public sealed class AuditionTransportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "trackdub-audition-transport-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
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
    public void Enrich_returns_bounded_file_urls_and_prioritizes_winner_audio()
    {
        using var store = new AuditionAudioStore(_folder, maxRetainedBytes: 32);
        byte[] loserBytes = new byte[40];
        byte[] winnerBytes = Encoding.ASCII.GetBytes("RIFFwinner");
        AuditionReport enriched = store.Enrich(Report(
            Take(1, loserBytes, winner: false),
            Take(2, winnerBytes, winner: true)));

        AuditionTakeReport loser = enriched.Segments[0].Takes[0];
        AuditionTakeReport winner = enriched.Segments[0].Takes[1];
        Assert.Null(loser.AudioUrl);
        Assert.Equal("audio_storage_limit", loser.AudioTransportOmitted);
        Assert.NotNull(winner.AudioUrl);
        Assert.Null(winner.AudioBytes);
        string id = winner.AudioUrl!.Split('/').Last();
        Assert.Equal(winnerBytes, File.ReadAllBytes(store.ResolvePath(id)!));
    }

    [Fact]
    public async Task Audio_endpoint_serves_byte_ranges_and_unknown_ids_are_not_found()
    {
        using var factory = new ServerFactory(new DelegatingHandlerStub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        using HttpClient client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<AuditionAudioStore>();
        AuditionReport enriched = store.Enrich(Report(Take(1, Encoding.ASCII.GetBytes("RIFF0123456789"), winner: true)));
        string url = enriched.Segments[0].Takes[0].AudioUrl!;

        using var range = new HttpRequestMessage(HttpMethod.Get, url);
        range.Headers.Range = new RangeHeaderValue(0, 3);
        using HttpResponseMessage response = await client.SendAsync(range);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        Assert.Equal("RIFF", await response.Content.ReadAsStringAsync());

        HttpResponseMessage missing = await client.GetAsync("/api/audition-audio/00000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Manifest_strips_browser_audio_transport_fields()
    {
        AuditionReport report = Report(Take(1, Encoding.ASCII.GetBytes("RIFFtransport"), winner: true));
        _ = new AuditionAudioStore(_folder).Enrich(report);
        string manifestPath = await AuditionOutputWriter.WriteAsync(report, _folder);
        string manifest = await File.ReadAllTextAsync(manifestPath);

        Assert.DoesNotContain("audio_data_url", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("audio_url", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("audio_transport_omitted", manifest, StringComparison.Ordinal);
        Assert.Contains("audio_sha256", manifest, StringComparison.Ordinal);
    }
}
