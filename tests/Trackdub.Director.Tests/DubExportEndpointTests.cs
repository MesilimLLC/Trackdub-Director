using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trackdub.Director.Core;
using static Trackdub.Director.Tests.ServerTestSupport;

namespace Trackdub.Director.Tests;

public sealed class DubExportEndpointTests
{
    private sealed class FakeDubExportService : IDubExportService
    {
        public string? VideoFileName { get; private set; }
        public byte[] VideoBytes { get; private set; } = [];
        public IReadOnlyList<string> ExpectedIds { get; private set; } = [];
        public IReadOnlyList<DubExportSegmentInput> Segments { get; private set; } = [];

        public async Task<Stream> RenderAsync(
            Stream video,
            string videoFileName,
            IReadOnlyList<string> expectedSegmentIds,
            IReadOnlyList<DubExportSegmentInput> segments,
            CancellationToken cancellationToken = default)
        {
            VideoFileName = videoFileName;
            using var copy = new MemoryStream();
            await video.CopyToAsync(copy, cancellationToken);
            VideoBytes = copy.ToArray();
            ExpectedIds = expectedSegmentIds;
            Segments = segments;
            return new MemoryStream(Encoding.ASCII.GetBytes("fake-mp4"));
        }
    }

    private sealed class ExportFactory(FakeDubExportService exportService) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<HttpClient>();
                services.AddSingleton<HttpClient>(_ => new HttpClient(
                    new DelegatingHandlerStub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))))
                { Timeout = TimeSpan.FromSeconds(60) });
                services.RemoveAll<IDubExportService>();
                services.AddSingleton<IDubExportService>(exportService);
            });
        }
    }

    private static byte[] Wav()
    {
        return AudioDuration.ToWav(
            new byte[9600],
            new PcmFormat { SampleRate = 48000, Channels = 1, BitsPerSample = 16 });
    }

    [Fact]
    public async Task ExportDub_accepts_multipart_video_and_winner_wav()
    {
        var fake = new FakeDubExportService();
        using var factory = new ExportFactory(fake);
        using HttpClient client = factory.CreateClient();
        using var form = new MultipartFormDataContent();
        var video = new ByteArrayContent([0, 0, 0, 24, 1, 2, 3]);
        video.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
        form.Add(video, "video", "original.mp4");
        form.Add(new StringContent("[\"seg-001\"]"), "segment_ids");
        form.Add(new StringContent("[{\"id\":\"seg-001\",\"start_seconds\":0.25,\"audio_field\":\"audio_0\"}]"), "placements");
        var audio = new ByteArrayContent(Wav());
        audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "audio_0", "seg-001-take1.wav");

        HttpResponseMessage response = await client.PostAsync("/api/export-dub", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("trackdub-dub.mp4", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("fake-mp4", await response.Content.ReadAsStringAsync());
        Assert.Equal("original.mp4", fake.VideoFileName);
        Assert.Equal(7, fake.VideoBytes.Length);
        Assert.Equal(["seg-001"], fake.ExpectedIds);
        DubExportSegmentInput segment = Assert.Single(fake.Segments);
        Assert.Equal(0.25, segment.StartSeconds);
        Assert.NotNull(segment.WavAudio);
        Assert.True(segment.WavAudio!.Length > 44);
    }
}
