namespace Trackdub.Director.Core;

/// <summary>
/// ASR diarization client for the transcribe stage: POST /v1/asr/transcribe with
/// {"mode":"DIARIZATION","model":"muse-voice-transcribe-1.0","audioEncoding":"WAV"},
/// the same endpoint and mode dataset prep used (samples/voa10/NOTES.md §3).
/// Implementations fail closed with <see cref="MetaApiException"/> on transport or
/// shape errors. The endpoint reports no token usage; results carry none.
/// </summary>
public interface IMuseTranscribeClient
{
    /// <summary>Transcribe one WAV audio stream with diarization. Returns the raw parsed response; segment mapping happens in <see cref="TranscribeService"/>.</summary>
    Task<TranscribeRawResponse> TranscribeAsync(Stream wavAudio, string? fileName = null, CancellationToken cancellationToken = default);
}
