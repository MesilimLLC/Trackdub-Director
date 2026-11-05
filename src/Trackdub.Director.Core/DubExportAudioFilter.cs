using System.Globalization;
using System.Text;

namespace Trackdub.Director.Core;

public static partial class DubExportPlanner
{
    public static string BuildAudioFilter(DubExportPlan plan, double videoDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string wholeDuration = FormatSeconds(videoDurationSeconds);
        string filter = string.Join(";", plan.Placements.Select(PlacementFilter));
        return plan.Placements.Count == 1
            ? $"{filter};[a0]atrim=0:{wholeDuration}[aout]"
            : $"{filter};{BuildMixFilter(plan.Placements.Count, wholeDuration)}";

        string PlacementFilter(DubExportPlacement placement, int index)
        {
            long delayMs = checked((long)Math.Round(placement.StartSeconds * 1000, MidpointRounding.AwayFromZero));
            return $"[{index + 1}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,"
                + $"adelay={delayMs}|{delayMs},apad=whole_dur={wholeDuration}[a{index}]";
        }
    }

    private static string BuildMixFilter(int count, string wholeDuration)
    {
        var inputs = new StringBuilder();
        for (int i = 0; i < count; i++) inputs.Append(CultureInfo.InvariantCulture, $"[a{i}]");
        return $"{inputs}amix=inputs={count}:duration=longest:normalize=0:dropout_transition=0,atrim=0:{wholeDuration}[aout]";
    }
}
