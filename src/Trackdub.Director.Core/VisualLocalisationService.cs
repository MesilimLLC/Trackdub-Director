namespace Trackdub.Director.Core;

/// <summary>
    /// Frame-level visual localisation by crop-edit-composite. The sign box plus
    /// padding is cropped, edited by the image model (template image-edit-v2: the
    /// probe's working instruction, verbatim), resampled back to the crop size,
    /// and only the exact box interior is pasted into a copy of the source — so
    /// pixels outside the box provably come from the source, never from model
    /// output. The outside-box diff is still computed and stored (0 by
    /// construction). Any missing/undecodable/undersized edit fails the segment
    /// with the reason recorded; an unmodified frame is never passed off as
    /// localised.
    /// </summary>
public sealed class VisualLocalisationService : IDisposable
{
    /// <summary>Template "image-edit-v2": generic visible-sign text replacement with source/target filled in.</summary>
    public const string PromptTemplateVersion = "image-edit-v2";

    /// <summary>Context padding (pixels) around the box sent to the model; only the box interior is composited back.</summary>
    public const int CropPaddingPixels = 20;

    public const int MaxFrameBytes = 10 * 1024 * 1024;

    public const int MaxFrameDimension = 4096;

    public const int MaxFramePixels = 16_777_216;

    public const int MinBoxPixels = 8;

    private readonly IMuseImageClient _client;
    private readonly bool _ownsClient;

    public VisualLocalisationService(IMuseImageClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public VisualLocalisationService()
        : this(new MetaMuseImageClient())
    {
        _ownsClient = true;
    }

    public void Dispose()
    {
        if (_ownsClient && _client is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public static string BuildInstruction(string sourceText, string targetText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetText);
        return $"replace the visible sign text \"{sourceText.Trim()}\" with \"{targetText.Trim()}\", preserving everything else in the image exactly";
    }

    /// <summary>
    /// Localise one decoded frame. The first two <paramref name="texts"/> entries
    /// are the source and target sign text; missing text is rejected during validation.
    /// </summary>
    public Task<VisualLocalisationSegmentResult> LocaliseRasterAsync(
        string segmentId,
        RasterImage frame,
        LocalisationBox box,
        params string[] texts)
    {
        string sourceText = texts.Length > 0 ? texts[0] : string.Empty;
        string targetText = texts.Length > 1 ? texts[1] : string.Empty;
        var context = new LocalisationContext
        {
            SegmentId = segmentId,
            Frame = frame,
            Box = box,
            SourceText = sourceText,
            TargetText = targetText,
            EndpointHost = GetEndpointHost(),
        };
        return LocaliseRasterCoreAsync(context, CancellationToken.None);
    }

    public async Task<VisualLocalisationBatch> RunAsync(
        IReadOnlyList<VisualLocalisationSegmentInput> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            throw new ArgumentException("At least one segment is required.", nameof(segments));
        }

        var results = new List<VisualLocalisationSegmentResult>(segments.Count);
        foreach (VisualLocalisationSegmentInput segment in segments)
        {
            results.Add(await ProcessInputAsync(segment, cancellationToken).ConfigureAwait(false));
        }

        int prompt = results.Sum(r => r.Usage.PromptTokens);
        int completion = results.Sum(r => r.Usage.CompletionTokens);
        int total = results.Sum(r => r.Usage.TotalTokens);
        return new VisualLocalisationBatch(
            results.ToArray(),
            new TranslationUsageSummary(prompt, completion, total),
            results.Sum(r => r.ImageCalls));
    }

    private async Task<VisualLocalisationSegmentResult> LocaliseRasterCoreAsync(
        LocalisationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.SegmentId);
        ArgumentNullException.ThrowIfNull(context.Frame);
        ArgumentNullException.ThrowIfNull(context.Box);

        string? boxError = ValidateBox(context.Box, context.Frame);
        if (boxError is not null)
        {
            return Failure(context, boxError);
        }

        if (string.IsNullOrWhiteSpace(context.SourceText) || string.IsNullOrWhiteSpace(context.TargetText))
        {
            return Failure(context, "Source and target text are required.");
        }

        return await EditAndCompositeAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VisualLocalisationSegmentResult> EditAndCompositeAsync(
        LocalisationContext context,
        CancellationToken cancellationToken)
    {
        RasterImage frame = context.Frame!;
        LocalisationBox box = context.Box!;
        int cropX = Math.Max(0, box.X1 - CropPaddingPixels);
        int cropY = Math.Max(0, box.Y1 - CropPaddingPixels);
        int cropX2 = Math.Min(frame.Width, box.X2 + CropPaddingPixels);
        int cropY2 = Math.Min(frame.Height, box.Y2 + CropPaddingPixels);
        RasterImage crop = frame.Crop(cropX, cropY, cropX2 - cropX, cropY2 - cropY);

        MuseImageEditResult edit;
        try
        {
            edit = await _client.EditAsync(crop, BuildInstruction(context.SourceText!, context.TargetText!), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MetaApiException or HttpRequestException or InvalidOperationException)
        {
            return Failure(context, ex.Message, imageCalls: 1);
        }

        int boxWidth = box.X2 - box.X1;
        int boxHeight = box.Y2 - box.Y1;
        if (edit.Image.Width < boxWidth || edit.Image.Height < boxHeight)
        {
            return Failure(
                context,
                $"Edited image is {edit.Image.Width}x{edit.Image.Height}, smaller than the target box {boxWidth}x{boxHeight}.",
                edit,
                imageCalls: 1);
        }

        RasterImage resized = edit.Image.ResizeBilinear(crop.Width, crop.Height);
        RasterImage patch = resized.Crop(box.X1 - cropX, box.Y1 - cropY, boxWidth, boxHeight);
        if (patch.Rgb.AsSpan().SequenceEqual(frame.Crop(box.X1, box.Y1, boxWidth, boxHeight).Rgb))
        {
            return Failure(context, "Edited image did not change the target box.", edit, imageCalls: 1);
        }

        RasterImage localised = frame.Paste(patch, box.X1, box.Y1);
        int outsideDiff = localised.CountDiffOutsideBox(frame, box.X1, box.Y1, box.X2, box.Y2);

        byte[] png = PngCodec.Encode(localised);
        return new VisualLocalisationSegmentResult(
            context.SegmentId,
            VisualLocalisationStatus.Localised,
            "data:image/png;base64," + Convert.ToBase64String(png),
            box,
            context.SourceText!.Trim(),
            context.TargetText!.Trim(),
            outsideDiff,
            Error: null,
            new VisualLocalisationProvenance(edit.Model, context.EndpointHost, DateTime.UtcNow.ToString("O"), PromptTemplateVersion),
            ToUsage(edit.Usage),
            ImageCalls: 1,
            png);
    }

    private async Task<VisualLocalisationSegmentResult> ProcessInputAsync(
        VisualLocalisationSegmentInput segment,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment.Id);

        string? validationError = ValidateSegment(segment, out RasterImage? frame);
        if (validationError is not null)
        {
            return Failure(CreateContext(segment, null), validationError);
        }

        return await LocaliseRasterCoreAsync(CreateContext(segment, frame), cancellationToken).ConfigureAwait(false);
    }

    private static LocalisationContext CreateContext(VisualLocalisationSegmentInput segment, RasterImage? frame)
    {
        return new LocalisationContext
        {
            SegmentId = segment.Id,
            Frame = frame,
            Box = segment.Box,
            SourceText = segment.SourceText,
            TargetText = segment.TargetText,
            EndpointHost = GetEndpointHost(),
        };
    }

    private static string? ValidateSegment(VisualLocalisationSegmentInput segment, out RasterImage? frame)
    {
        frame = null;
        if (segment.Box is null) return "Each segment needs a box plus source_text and target_text.";
        if (string.IsNullOrWhiteSpace(segment.SourceText)) return "Each segment needs a box plus source_text and target_text.";
        if (string.IsNullOrWhiteSpace(segment.TargetText)) return "Each segment needs a box plus source_text and target_text.";
        return DecodeFrame(new FrameDataUrl(segment.FrameImageDataUrl), out frame);
    }

    private static string? DecodeFrame(FrameDataUrl dataUrl, out RasterImage? frame)
    {
        frame = null;
        if (!TryDecodeDataUrl(dataUrl, out byte[]? frameBytes, out string? decodeError))
        {
            return decodeError!;
        }

        if (frameBytes!.Length > MaxFrameBytes)
        {
            return $"Frame image exceeds the {MaxFrameBytes} byte limit.";
        }

        if (!MatchesDeclaredImageType(dataUrl, frameBytes))
        {
            return "Frame image bytes do not match the declared PNG, JPEG, or WebP type.";
        }

        try
        {
            frame = WicImageDecoder.Decode(frameBytes);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        return ValidateDimensions(frame);
    }

    private static string? ValidateDimensions(RasterImage frame)
    {
        if (frame.Width > MaxFrameDimension || frame.Height > MaxFrameDimension)
        {
            return $"Frame dimensions {frame.Width}x{frame.Height} exceed the {MaxFrameDimension} pixel limit.";
        }

        if ((long)frame.Width * frame.Height > MaxFramePixels)
        {
            return $"Frame dimensions {frame.Width}x{frame.Height} exceed the {MaxFramePixels} total pixel limit.";
        }

        return null;
    }

    private static VisualLocalisationSegmentResult Failure(
        LocalisationContext context,
        string error,
        MuseImageEditResult? edit = null,
        int imageCalls = 0)
    {
        string modelId = edit?.Model ?? MetaMuseImageClient.GetImageModel();
        return new VisualLocalisationSegmentResult(
            context.SegmentId,
            VisualLocalisationStatus.Failed,
            LocalisedImageDataUrl: null,
            context.Box,
            context.SourceText,
            context.TargetText,
            OutsideBoxDiff: null,
            error.Length > 500 ? error[..500] : error,
            new VisualLocalisationProvenance(modelId, context.EndpointHost, DateTime.UtcNow.ToString("O"), PromptTemplateVersion),
            ToUsage(edit?.Usage),
            imageCalls);
    }

    private static string? ValidateBox(LocalisationBox box, RasterImage frame)
    {
        if (!IsInsideFrame(box, frame))
        {
            return $"Box ({box.X1},{box.Y1})-({box.X2},{box.Y2}) does not lie inside the {frame.Width}x{frame.Height} frame.";
        }

        if (!IsLargeEnough(box))
        {
            return $"Box must be at least {MinBoxPixels}x{MinBoxPixels} pixels.";
        }

        return null;
    }

    private static bool IsInsideFrame(LocalisationBox box, RasterImage frame)
    {
        if (box.X1 < 0) return false;
        if (box.Y1 < 0) return false;
        if (box.X2 <= box.X1) return false;
        if (box.Y2 <= box.Y1) return false;
        if (box.X2 > frame.Width) return false;
        return box.Y2 <= frame.Height;
    }

    private static bool IsLargeEnough(LocalisationBox box)
    {
        if (box.X2 - box.X1 < MinBoxPixels) return false;
        return box.Y2 - box.Y1 >= MinBoxPixels;
    }

    private static bool MatchesDeclaredImageType(FrameDataUrl dataUrl, byte[] bytes)
    {
        string value = dataUrl.Value ?? string.Empty;
        if (value.StartsWith("data:image/png;", StringComparison.OrdinalIgnoreCase)) return HasPngSignature(bytes);
        if (value.StartsWith("data:image/jpeg;", StringComparison.OrdinalIgnoreCase)) return HasJpegSignature(bytes);
        return HasWebpSignature(bytes);
    }

    private static bool HasPngSignature(byte[] bytes)
    {
        return bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    }

    private static bool HasJpegSignature(byte[] bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
    }

    private static bool HasWebpSignature(byte[] bytes)
    {
        return bytes.Length >= 12
            && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);
    }

    private static bool TryDecodeDataUrl(FrameDataUrl dataUrl, out byte[]? bytes, out string? error)
    {
        bytes = null;
        error = null;
        string? value = dataUrl.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Frame image is required (data:image data URL).";
            return false;
        }

        string trimmed = value.Trim();
        int marker = trimmed.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        bool supported = trimmed.StartsWith("data:image/png;", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("data:image/jpeg;", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("data:image/webp;", StringComparison.OrdinalIgnoreCase);
        if (!supported)
        {
            error = "Frame image must be a base64 PNG, JPEG, or WebP data URL.";
            return false;
        }

        if (marker < 0)
        {
            error = "Frame image must be a base64 PNG, JPEG, or WebP data URL.";
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(trimmed[(marker + ";base64,".Length)..]);
            return true;
        }
        catch (FormatException)
        {
            error = "Frame data URL contains invalid base64.";
            return false;
        }
    }

    private static TranslationUsage ToUsage(MetaClient.TokenUsage? usage)
    {
        return new TranslationUsage(
            usage?.PromptTokens ?? 0,
            usage?.CompletionTokens ?? 0,
            usage?.TotalTokens ?? ((usage?.PromptTokens ?? 0) + (usage?.CompletionTokens ?? 0)));
    }

    private static string GetEndpointHost()
    {
        string baseUrl = MetaAuth.GetBaseUrl();
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ? uri.Host : baseUrl;
    }

    private sealed class LocalisationContext
    {
        public required string SegmentId { get; init; }
        public RasterImage? Frame { get; init; }
        public LocalisationBox? Box { get; init; }
        public string? SourceText { get; init; }
        public string? TargetText { get; init; }
        public required string EndpointHost { get; init; }
    }

    private readonly record struct FrameDataUrl(string? Value);
}
