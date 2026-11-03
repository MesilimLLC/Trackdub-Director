namespace Trackdub.Director.Core;

/// <summary>
/// Pipeline orchestration: runs the existing lanes in order by calling the
/// existing services — transcribe (only when audio is supplied), then
/// translation + QA (the QA lane includes the translation calls), then scene for
/// the segments that have frames, then audition only when explicitly flagged on.
/// No lane logic is duplicated or forked here; each lane's report keeps its
/// existing shape and verdicts. A stage skipped by flag is recorded as skipped,
/// never as a pass or a failure; a failed stage stops the run and downstream
/// stages are recorded NotRun and never called. Director adaptation stays out of
/// the pipeline (per-line opt-in by design).
/// </summary>
public sealed class PipelineService
{
    public const string StageTranscribe = "transcribe";
    public const string StageQa = "qa";
    public const string StageScene = "scene";
    public const string StageAudition = "audition";

    private readonly MetaClient _client;
    private readonly TranscribeService _transcribe;
    private readonly ITtsProvider? _tts;
    private readonly QaOptions _options;

    public PipelineService(MetaClient client, IMuseTranscribeClient transcribeClient, ITtsProvider? tts, QaOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _transcribe = new TranscribeService(transcribeClient ?? throw new ArgumentNullException(nameof(transcribeClient)));
        _tts = tts;
        _options = options ?? QaOptions.Default;
    }

    public async Task<PipelineReport> RunAsync(PipelineRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetLanguage);

        if (request.AudioWav is null && request.Segments is not { Count: > 0 })
        {
            throw new ArgumentException("Pipeline needs audio or a non-empty segments array.", nameof(request));
        }

        var state = new PipelineRunState();
        if (!await RunTranscribeAsync(request, state, cancellationToken).ConfigureAwait(false))
        {
            return state.ToReport(request);
        }

        if (!await RunQaAsync(request, state, cancellationToken).ConfigureAwait(false))
        {
            return state.ToReport(request);
        }

        if (!await RunSceneAsync(request, state, cancellationToken).ConfigureAwait(false))
        {
            return state.ToReport(request);
        }

        await RunAuditionAsync(request, state, cancellationToken).ConfigureAwait(false);
        return state.ToReport(request);
    }

    // Stage 1: transcribe (only when audio is supplied).
    private async Task<bool> RunTranscribeAsync(
        PipelineRequest request,
        PipelineRunState state,
        CancellationToken cancellationToken)
    {
        if (request.AudioWav is null)
        {
            state.Stages.Add(new PipelineStageReport(StageTranscribe, PipelineStageStatus.NotRun, Note: "no audio supplied"));
            state.Segments = request.Segments!.Select(s => new TranscribeSegment(s.Id, s.Text, s.DurationSeconds, s.Speaker)).ToArray();
            if (state.Segments.Length == 0)
            {
                throw new ArgumentException("Pipeline produced no segments to process.", nameof(request));
            }

            return true;
        }

        TranscribeResult transcribe;
        try
        {
            using var stream = new MemoryStream(request.AudioWav, writable: false);
            transcribe = await _transcribe.TranscribeAsync(stream, request.AudioFileName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MetaApiException or InvalidOperationException)
        {
            state.Stages.Add(new PipelineStageReport(StageTranscribe, PipelineStageStatus.Failed, Error: ex.Message));
            AppendNotRun(state.Stages, StageQa, StageScene, StageAudition);
            return false;
        }

        state.Transcribe = transcribe;
        state.Segments = transcribe.Segments;
        if (state.Segments.Length == 0)
        {
            state.Stages.Add(new PipelineStageReport(StageTranscribe, PipelineStageStatus.Failed, Error: "No speech turns detected in audio."));
            AppendNotRun(state.Stages, StageQa, StageScene, StageAudition);
            return false;
        }

        state.Stages.Add(new PipelineStageReport(StageTranscribe, PipelineStageStatus.Ran));
        return true;
    }

    // Stage 2: translation + QA (QaService performs the translation calls).
    private async Task<bool> RunQaAsync(
        PipelineRequest request,
        PipelineRunState state,
        CancellationToken cancellationToken)
    {
        var qaService = new QaService(_client, _options);
        QaReport qa;
        try
        {
            qa = await qaService.RunAsync(
                state.Segments.Select(s => new QaSegmentInput(s.Id, s.Text, s.DurationSeconds)).ToArray(),
                request.SourceLanguage,
                request.TargetLanguage,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MetaApiException or ArgumentException)
        {
            state.Stages.Add(new PipelineStageReport(StageQa, PipelineStageStatus.Failed, Error: ex.Message));
            AppendNotRun(state.Stages, StageScene, StageAudition);
            return false;
        }

        state.Qa = qa;
        if (TerminalQaError(qa) is { } terminalError)
        {
            state.Stages.Add(new PipelineStageReport(StageQa, PipelineStageStatus.Failed, Error: terminalError));
            AppendNotRun(state.Stages, StageScene, StageAudition);
            return false;
        }

        state.Stages.Add(new PipelineStageReport(StageQa, PipelineStageStatus.Ran));
        return true;
    }

    // Stage 3: scene context for segments that have frames.
    private async Task<bool> RunSceneAsync(
        PipelineRequest request,
        PipelineRunState state,
        CancellationToken cancellationToken)
    {
        if (!request.Scene)
        {
            state.Stages.Add(new PipelineStageReport(StageScene, PipelineStageStatus.SkippedByFlag));
            return true;
        }

        SceneSegmentInput[] framed = FramedSceneSegments(request, state.Segments);

        if (framed.Length == 0)
        {
            state.Stages.Add(new PipelineStageReport(StageScene, PipelineStageStatus.Ran, Note: "no segments with frames"));
            return true;
        }

        SceneContextBatch scene;
        try
        {
            scene = await new SceneContextService(_client).RunAsync(framed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MetaApiException or ArgumentException)
        {
            state.Stages.Add(new PipelineStageReport(StageScene, PipelineStageStatus.Failed, Error: ex.Message));
            AppendNotRun(state.Stages, StageAudition);
            return false;
        }

        state.Scene = scene;
        SceneSegmentResult? failed = FailedSceneSegment(scene);
        if (failed is not null)
        {
            state.Stages.Add(new PipelineStageReport(
                StageScene,
                PipelineStageStatus.Failed,
                Error: failed.Error ?? $"Scene context failed for segment '{failed.Id}'."));
            AppendNotRun(state.Stages, StageAudition);
            return false;
        }

        state.Stages.Add(new PipelineStageReport(StageScene, PipelineStageStatus.Ran));
        return true;
    }

    // Stage 4: audition, only when explicitly flagged on. A missing TTS key
    // keeps the stage skipped (recorded as skipped, never as pass or failure).
    private async Task RunAuditionAsync(
        PipelineRequest request,
        PipelineRunState state,
        CancellationToken cancellationToken)
    {
        if (!request.Audition)
        {
            state.Stages.Add(new PipelineStageReport(StageAudition, PipelineStageStatus.SkippedByFlag));
            return;
        }

        if (_tts is null || !ElevenLabsAuth.HasApiKey())
        {
            state.Stages.Add(new PipelineStageReport(StageAudition, PipelineStageStatus.SkippedByFlag, Note: "ELEVENLABS_API_KEY not set"));
            return;
        }

        var auditionService = new AuditionService(
            _client,
            _tts,
            _options,
            new AuditionRuntimeOptions(EmotionTags: EmotionTagOptions.EnabledDefault));
        try
        {
            state.Audition = await auditionService.RunAsync(
                BuildAuditionInputs(state),
                request.SourceLanguage,
                request.TargetLanguage,
                cancellationToken).ConfigureAwait(false);
            state.Stages.Add(new PipelineStageReport(StageAudition, PipelineStageStatus.Ran));
        }
        catch (Exception ex) when (ex is TtsApiException or MetaApiException or ArgumentException)
        {
            state.Stages.Add(new PipelineStageReport(StageAudition, PipelineStageStatus.Failed, Error: ex.Message));
        }
    }

    private static SceneSegmentInput[] FramedSceneSegments(PipelineRequest request, TranscribeSegment[] segments)
    {
        var ids = new HashSet<string>(segments.Select(s => s.Id), StringComparer.Ordinal);
        return (request.SceneSegments ?? [])
            .Where(s => HasFrame(s, ids))
            .ToArray();
    }

    private static bool HasFrame(SceneSegmentInput? segment, HashSet<string> ids)
    {
        if (segment is null)
        {
            return false;
        }

        if (!ids.Contains(segment.Id))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(segment.FrameImageDataUrl);
    }

    private static SceneSegmentResult? FailedSceneSegment(SceneContextBatch scene)
    {
        return scene.Segments.FirstOrDefault(s => s.Status == SceneContextStatus.Failed);
    }

    private static AuditionSegmentInput[] BuildAuditionInputs(PipelineRunState state)
    {
        return state.Segments.Select(s => new AuditionSegmentInput(
            s.Id,
            s.Text,
            s.DurationSeconds,
            s.Speaker,
            state.Scene?.Segments.FirstOrDefault(sc => sc.Id == s.Id)?.Context?.EmotionToneLabel,
            state.Scene?.Segments.FirstOrDefault(sc => sc.Id == s.Id)?.Context?.PacingLabel)).ToArray();
    }

    /// <summary>
    /// An upstream failure in the QA report that is terminal for the pipeline: a translation
    /// generation that failed outright (null first_attempt) or a meaning check that errored.
    /// Downstream stages (scene context, audition) need trustworthy translations to run, so
    /// either condition gates the run.
    /// </summary>
    private static string? TerminalQaError(QaReport qa)
    {
        if (qa.Segments.Any(s => s.FirstAttempt is null))
        {
            return "A translation generation failed upstream.";
        }

        foreach (QaSegmentReport segment in qa.Segments)
        {
            // A failed generation carries no attempts; only a completed attempt can hold a meaning error.
            if (segment.FirstAttempt?.MeaningVerdict == MeaningVerdict.Error)
            {
                return "Meaning check failed with an upstream error.";
            }

            if (segment.Revisions.Any(r => r.MeaningVerdict == MeaningVerdict.Error))
            {
                return "Meaning check failed with an upstream error.";
            }
        }

        return null;
    }

    private static void AppendNotRun(List<PipelineStageReport> stages, params string[] stageNames)
    {
        foreach (string name in stageNames)
        {
            stages.Add(new PipelineStageReport(name, PipelineStageStatus.NotRun, Note: "upstream stage failed"));
        }
    }

    // Mutable per-run state shared by the stage helpers.
    private sealed class PipelineRunState
    {
        public List<PipelineStageReport> Stages { get; } = new(4);

        public TranscribeSegment[] Segments { get; set; } = [];

        public TranscribeResult? Transcribe { get; set; }

        public QaReport? Qa { get; set; }

        public SceneContextBatch? Scene { get; set; }

        public AuditionReport? Audition { get; set; }

        public PipelineReport ToReport(PipelineRequest request)
        {
            return new PipelineReport(
                request.SourceLanguage,
                request.TargetLanguage,
                Stages.ToArray(),
                Segments,
                Transcribe,
                Qa,
                Scene,
                Audition);
        }
    }
}
