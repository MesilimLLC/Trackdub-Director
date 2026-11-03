using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>
/// Configuration for the computed-speed retake path in the audition stage.
/// Disabled by default in shipped builds; enabling is per-run configuration.
/// </summary>
public sealed record SpeedRetakeOptions(
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("cap")] double Cap = SpeedRetakeMath.DefaultCap,
    [property: JsonPropertyName("max_retakes_per_segment")] int MaxRetakesPerSegment = 1)
{
    public static readonly SpeedRetakeOptions Disabled = new();
}

/// <summary>
/// One append-only calibration sample for a (voice, model) pair: what was
/// requested, what the 1.0 measurement predicted, and what was realized.
/// </summary>
public sealed record SpeedCalibrationSample(
    [property: JsonPropertyName("voice_id")] string VoiceId,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("requested_speed")] double RequestedSpeed,
    [property: JsonPropertyName("predicted_duration")] double PredictedDuration,
    [property: JsonPropertyName("realized_duration")] double RealizedDuration,
    [property: JsonPropertyName("realized_ratio")] double RealizedRatio);

/// <summary>Inputs for a computed-speed request: the measured take, its window, the cap, and calibration context.</summary>
public sealed record SpeedRetakeRequest(
    double MeasuredSeconds,
    double WindowSeconds,
    double Cap,
    IReadOnlyList<SpeedCalibrationSample> Samples,
    string VoiceId,
    string ModelId);

/// <summary>
/// Pure math for the computed-speed retake: factor from a measured take, the
/// calibration correction, and the requested-speed clamp. No provider calls.
/// </summary>
public static class SpeedRetakeMath
{
    public const double DefaultCap = 1.10;
    public const double HardCeiling = 1.20;
    public const int MinSamplesForCorrection = 3;

    /// <summary>The measured overshoot factor f = measured / window.</summary>
    public static double Factor(double measuredSeconds, double windowSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSeconds);

        return measuredSeconds / windowSeconds;
    }

    /// <summary>
    /// Median realized-vs-predicted ratio (predicted_duration / realized_duration)
    /// across samples, clamped to a [0.95, 1.05] correction. An ideal response
    /// realizes exactly the predicted duration, so the ratio is 1.0 and the
    /// requested speed is the raw overshoot factor. Fewer than
    /// <see cref="MinSamplesForCorrection"/> samples: no correction (1.0).
    /// </summary>
    public static double CorrectionFactor(IReadOnlyList<SpeedCalibrationSample> samples, string voiceId, string modelId)
    {
        double[] ratios = samples
            .Where(s => string.Equals(s.VoiceId, voiceId, StringComparison.Ordinal)
                && string.Equals(s.ModelId, modelId, StringComparison.Ordinal)
                && s.RequestedSpeed > 0)
            .Select(s => s.PredictedDuration / s.RealizedDuration)
            .OrderBy(v => v)
            .ToArray();
        if (ratios.Length < MinSamplesForCorrection)
        {
            return 1.0;
        }

        double median = ratios.Length % 2 == 1
            ? ratios[ratios.Length / 2]
            : (ratios[(ratios.Length / 2) - 1] + ratios[ratios.Length / 2]) / 2.0;
        return Math.Clamp(median, 0.95, 1.05);
    }

    /// <summary>
    /// The speed to request for a take that measured <paramref name="measuredSeconds"/>
    /// against <paramref name="windowSeconds"/>: f corrected by calibration,
    /// rounded to 3 decimals, clamped to [1.0, cap] with cap itself clamped to
    /// the provider's documented hard ceiling. This path never requests below 1.0.
    /// </summary>
    public static double RequestedSpeed(
        SpeedRetakeRequest request,
        out double computedFactor,
        out bool calibrationCorrected)
    {
        computedFactor = Factor(request.MeasuredSeconds, request.WindowSeconds);
        double effectiveCap = Math.Clamp(request.Cap, 1.0, HardCeiling);
        double correction = CorrectionFactor(request.Samples, request.VoiceId, request.ModelId);
        calibrationCorrected = correction != 1.0;
        double requested = Math.Round(computedFactor / correction, 3);
        return Math.Clamp(requested, 1.0, effectiveCap);
    }
}
