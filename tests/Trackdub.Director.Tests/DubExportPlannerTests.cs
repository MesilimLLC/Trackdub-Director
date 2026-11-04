using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class DubExportPlannerTests
{
    private static byte[] Wav(double seconds)
    {
        int sampleCount = (int)Math.Round(seconds * 48000);
        return AudioDuration.ToWav(
            new byte[sampleCount * 2],
            new PcmFormat { SampleRate = 48000, Channels = 1, BitsPerSample = 16 });
    }

    [Fact]
    public void Build_sorts_measured_winners_and_builds_delayed_mix_filter()
    {
        DubExportPlan plan = DubExportPlanner.Build(
            ["seg-001", "seg-002"],
            [
                new DubExportSegmentInput("seg-002", 2.5, Wav(1)),
                new DubExportSegmentInput("seg-001", 0.25, Wav(1.5)),
            ],
            videoDurationSeconds: 5);

        Assert.Equal(["seg-001", "seg-002"], plan.Placements.Select(p => p.Id));
        Assert.Equal(1.5, plan.Placements[0].DurationSeconds);
        Assert.Equal(1.75, plan.Placements[0].EndSeconds);

        string filter = DubExportPlanner.BuildAudioFilter(plan, 5);
        Assert.Contains("[1:a]", filter);
        Assert.Contains("adelay=250|250", filter);
        Assert.Contains("adelay=2500|2500", filter);
        Assert.Contains("amix=inputs=2", filter);
        Assert.Contains("[aout]", filter);
    }

    [Fact]
    public void Build_refuses_missing_winner_or_start()
    {
        ArgumentException missingWinner = Assert.Throws<ArgumentException>(() => DubExportPlanner.Build(
            ["seg-001", "seg-002"],
            [new DubExportSegmentInput("seg-001", 0, Wav(1))],
            videoDurationSeconds: 5));
        Assert.Contains("seg-002", missingWinner.Message);

        ArgumentException missingStart = Assert.Throws<ArgumentException>(() => DubExportPlanner.Build(
            ["seg-001"],
            [new DubExportSegmentInput("seg-001", null, Wav(1))],
            videoDurationSeconds: 5));
        Assert.Contains("explicit start_seconds", missingStart.Message);
    }

    [Fact]
    public void Build_refuses_overlap_and_audio_past_video_end()
    {
        ArgumentException overlap = Assert.Throws<ArgumentException>(() => DubExportPlanner.Build(
            ["seg-001", "seg-002"],
            [
                new DubExportSegmentInput("seg-001", 0, Wav(2)),
                new DubExportSegmentInput("seg-002", 1, Wav(1)),
            ],
            videoDurationSeconds: 5));
        Assert.Contains("overlap", overlap.Message);

        ArgumentException pastEnd = Assert.Throws<ArgumentException>(() => DubExportPlanner.Build(
            ["seg-001"],
            [new DubExportSegmentInput("seg-001", 4.5, Wav(1))],
            videoDurationSeconds: 5));
        Assert.Contains("after the 5s source video", pastEnd.Message);
    }

    [Fact]
    public void Build_refuses_non_wav_winner_audio()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => DubExportPlanner.Build(
            ["seg-001"],
            [new DubExportSegmentInput("seg-001", 0, [1, 2, 3, 4])],
            videoDurationSeconds: 5));

        Assert.Contains("WAV winner", error.Message);
    }
}
