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

        var stages = new List<PipelineStageReport>(4);
        TranscribeResult? transcribe = null;
        QaReport? qa = null;
        SceneContextBatch? scene = null;
        AuditionReport? audition = null;

        // Stage 1: transcribe (only when audio is supplied).
        TranscribeSegment[] segments;
        if (request.AudioWav is not null)
        {
            try
            {
                using var stream = new MemoryStream(request.AudioWav, writable: false);
                transcribe = await _transcribe.TranscribeAsync(stream, request.AudioFileName, cancellationToken).ConfigureAwait(false);
                stages.Add(new PipelineStageReport(StageTranscribe, PipelineStageStatus.Ran));
            }
            catch (Exception ex) when (ex is MetaApiException or InvalidOperationException)
            {
                stages.Add(new PipelineStageReport(StageTranscribe, PipelineStageStatus.Failed, Error: ex.Message));
                stages.Add(new PipelineStageReport(StageQa, PipelineStageStatus.NotRun, Note: "upstream stage failed"));
                stages.Add(new PipelineStageReport(StageScene, PipelineStageStatus.NotRun, Note: "upstream stage failed"));
                stages.Add(new PipelineStageReport(StageAudition, PipelineStageStatus.NotRun, Note: "upstream stage failed"));
                return new PipelineReport(request.SourceLanguage, request.TargetLanguage, stages.ToArray(), [], null, null, null, null);
            }

            segments = transcribe.Segments;
        }
        else
        {
            stages.Add(new PipelineStageReport(StageTranscribe, PipelineStageStatus.NotRun, Note: "no audio supplied"));
            segments = request.Segments!.Select(s => new TranscribeSegment(s.Id, s.Text, s.DurationSeconds, s.Speaker)).ToArray();
        }

        if (segments.Length == 0)
        {
            throw new ArgumentException("Pipeline produced no segments to process.", nameof(request));
        }

        // Stage 2: translation + QA (QaService performs the translation calls).
        var qaService = new QaService(_client, _options);
        try
        {
            qa = await qaService.RunAsync(
                segments.Select(s => new QaSegmentInput(s.Id, s.Text, s.DurationSeconds)).ToArray(),
                request.SourceLanguage,
                request.TargetLanguage,
                cancellationToken).ConfigureAwait(false);
            stages.Add(new PipelineStageReport(StageQa, PipelineStageStatus.Ran));
        }
        catch (Exception ex) when (ex is MetaApiException or ArgumentException)
        {
            stages.Add(new PipelineStageReport(StageQa, PipelineStageStatus.Failed, Error: ex.Message));
            stages.Add(new PipelineStageReport(StageScene, PipelineStageStatus.NotRun, Note: "upstream stage failed"));
            stages.Add(new PipelineStageReport(StageAudition, PipelineStageStatus.NotRun, Note: "upstream stage failed"));
            return new PipelineReport(request.SourceLanguage, request.TargetLanguage, stages.ToArray(), segments, transcribe, null, null, null);
        }

        // Stage 3: scene context for segments that have frames.
        if (!request.Scene)
        {
            stages.Add(new PipelineStageReport(StageScene, PipelineStageStatus.SkippedByFlag));
        }
        else
        {
            var ids = new HashSet<string>(segments.Select(s => s.Id), StringComparer.Ordinal);
            SceneSegmentInput[] framed = (request.SceneSegments ?? [])
                .Where(s => s is not null && ids.Contains(s.Id) && !string.IsNullOrWhiteSpace(s.FrameImageDataUrl))
                .ToArray();

            if (framed.Length == 0)
            {
                stages.Add(new PipelineStageReport(StageScene, PipelineStageStatus.Ran, Note: "no segments with frames"));
            }
            else
            {
                var sceneService = new SceneContextService(_client);
                try
                {
                    scene = await sceneService.RunAsync(framed, cancellationToken).ConfigureAwait(false);
                    stages.Add(new PipelineStageReport(StageScene, PipelineStageStatus.Ran));
                }
                catch (Exception ex) when (ex is MetaApiException or ArgumentException)
                {
                    stages.Add(new PipelineStageReport(StageScene, PipelineStageStatus.Failed, Error: ex.Message));
                    stages.Add(new PipelineStageReport(StageAudition, PipelineStageStatus.NotRun, Note: "upstream stage failed"));
                    return new PipelineReport(request.SourceLanguage, request.TargetLanguage, stages.ToArray(), segments, transcribe, qa, null, null);
                }
            }
        }

        // Stage 4: audition, only when explicitly flagged on. A missing TTS key
        // keeps the stage skipped (recorded as skipped, never as pass or failure).
        if (!request.Audition)
        {
            stages.Add(new PipelineStageReport(StageAudition, PipelineStageStatus.SkippedByFlag));
        }
        else if (_tts is null || !ElevenLabsAuth.HasApiKey())
        {
            stages.Add(new PipelineStageReport(StageAudition, PipelineStageStatus.SkippedByFlag, Note: "ELEVENLABS_API_KEY not set"));
        }
        else
        {
            var auditionService = new AuditionService(_client, _tts, _options);
            try
            {
                audition = await auditionService.RunAsync(
                    segments.Select(s => new AuditionSegmentInput(s.Id, s.Text, s.DurationSeconds, s.Speaker)).ToArray(),
                    request.SourceLanguage,
                    request.TargetLanguage,
                    cancellationToken).ConfigureAwait(false);
                stages.Add(new PipelineStageReport(StageAudition, PipelineStageStatus.Ran));
            }
            catch (Exception ex) when (ex is TtsApiException or MetaApiException or ArgumentException)
            {
                stages.Add(new PipelineStageReport(StageAudition, PipelineStageStatus.Failed, Error: ex.Message));
            }
        }

        return new PipelineReport(request.SourceLanguage, request.TargetLanguage, stages.ToArray(), segments, transcribe, qa, scene, audition);
    }
}
