using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Tests for the static-shot report models: the shot-window boundary rule and the
/// wire names the report is read back with.
/// </summary>
public sealed class StaticShotModelsTests
{
    [Fact]
    public void Contains_includes_the_start_and_excludes_the_end()
    {
        var window = new StaticShotWindow(StartSeconds: 1.0, EndSeconds: 2.0);

        Assert.True(window.Contains(1.0));
        Assert.True(window.Contains(1.999));
        Assert.False(window.Contains(2.0));
    }

    [Theory]
    [InlineData(0.999)]
    [InlineData(2.5)]
    public void Contains_rejects_times_outside_the_window(double timeSeconds)
    {
        var window = new StaticShotWindow(StartSeconds: 1.0, EndSeconds: 2.0);

        Assert.False(window.Contains(timeSeconds));
    }

    [Fact]
    public void Window_serializes_with_the_documented_property_names()
    {
        string json = JsonSerializer.Serialize(new StaticShotWindow(StartSeconds: 1.5, EndSeconds: 3.25));

        Assert.Equal("{\"start_seconds\":1.5,\"end_seconds\":3.25}", json);
    }

    [Fact]
    public void Status_is_written_as_its_name_not_its_number()
    {
        Assert.Equal("\"Refused\"", JsonSerializer.Serialize(StaticShotStatus.Refused));
        Assert.Equal("\"Composited\"", JsonSerializer.Serialize(StaticShotStatus.Composited));
    }

    [Fact]
    public void Frame_round_trips_through_json()
    {
        var frame = new StaticShotFrame(
            FrameIndex: 7,
            TimeSeconds: 0.25,
            Ssim: 0.98,
            MaxBlockLumaShift: 3.0,
            Pass: true);

        string json = JsonSerializer.Serialize(frame);
        StaticShotFrame? parsed = JsonSerializer.Deserialize<StaticShotFrame>(json);

        Assert.NotNull(parsed);
        Assert.Equal(frame, parsed);
        Assert.Contains("\"frame_index\":7", json);
        Assert.Contains("\"max_block_luma_shift\":3", json);
        Assert.Contains("\"pass\":true", json);
    }

    [Fact]
    public void Report_serializes_a_refusal_and_round_trips()
    {
        var frame = new StaticShotFrame(FrameIndex: 3, TimeSeconds: 0.5, Ssim: 0.5, MaxBlockLumaShift: 12.0, Pass: false);
        var report = new StaticShotReport(
            Stage: "static-shot",
            Status: StaticShotStatus.Refused,
            Reason: "frame 3 failed the gate",
            Video: "scene.mp4",
            ReferenceFrame: "ref.png",
            LocalisedFrame: "localised.png",
            Box: new LocalisationBox(X1: 1, Y1: 2, X2: 30, Y2: 40),
            Window: new StaticShotWindow(StartSeconds: 1.0, EndSeconds: 2.0),
            Fps: 24.0,
            Metric: "box-ssim",
            MinSsimThreshold: 0.9,
            MaxBlockLumaShiftThreshold: 8.0,
            FramesChecked: 4,
            SsimMin: 0.5,
            SsimMinFrame: 3,
            SsimMean: 0.8,
            BlockLumaShiftMax: 12.0,
            BlockLumaShiftMaxFrame: 3,
            FirstFailingFrame: frame,
            FramesComposited: 0,
            OutsideBoxDiffMax: null,
            ImageCalls: 0,
            Output: null,
            Notes: ["gate refused"],
            TimestampUtc: "2026-01-01T00:00:00Z",
            Frames: [frame]);

        string json = JsonSerializer.Serialize(report);
        StaticShotReport? parsed = JsonSerializer.Deserialize<StaticShotReport>(json);

        Assert.Contains("\"status\":\"Refused\"", json);
        Assert.Contains("\"first_failing_frame\"", json);
        Assert.NotNull(parsed);
        Assert.Equal(StaticShotStatus.Refused, parsed.Status);
        Assert.Null(parsed.Output);
        Assert.Equal("gate refused", Assert.Single(parsed.Notes));
        Assert.Equal(frame, Assert.Single(parsed.Frames));
    }

    [Fact]
    public void BoxSsim_carries_both_measurements()
    {
        var box = new BoxSsim(Mean: 0.91, MaxBlockLumaShift: 12.5);

        Assert.Equal(0.91, box.Mean);
        Assert.Equal(12.5, box.MaxBlockLumaShift);
    }
}
