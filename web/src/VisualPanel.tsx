import {
  type SegmentInput,
  type VisualLocalisationSegmentResult,
} from "./api";

export function VisualStatusPill({ visual }: { visual: VisualLocalisationSegmentResult }) {
  const statusClass = visual.status === "Localised" ? "verdict-pass" : "verdict-fail";
  return (
    <span className={`verdict ${statusClass}`} title={`Visual localisation (${visual.provenance.model_id}, ${visual.provenance.prompt_template_version})`}>
      Visual: {visual.status}
    </span>
  );
}

export function VisualPanel({ seg, visual }: { seg: SegmentInput; visual: VisualLocalisationSegmentResult }) {
  return (
    <div className="attempts-panel" aria-label={`Visual localisation for ${seg.id}`}>
      {visual.status === "Failed" ? (
        <div className="meaning-issues">
          <strong>Visual localisation failed:</strong> {visual.error ?? "unknown error"}
        </div>
      ) : (
        <div className="director-grid">
          <div>
            <div className="director-label">Before · {visual.source_text ?? "—"}</div>
            {seg.frame_image_data_url ? (
              <img className="visual-frame" src={seg.frame_image_data_url} alt={`Source frame for ${seg.id}`} />
            ) : (
              <div className="token-usage">No source frame</div>
            )}
          </div>
          <div>
            <div className="director-label">After · {visual.target_text ?? "—"}</div>
            {visual.localised_image_data_url ? (
              <>
                <img className="visual-frame" src={visual.localised_image_data_url} alt={`Localised frame for ${seg.id}`} />
                <a className="btn small" href={visual.localised_image_data_url} download={`${seg.id}-localised.png`}>Download PNG</a>
              </>
            ) : (
              <div className="token-usage">No localised frame</div>
            )}
          </div>
        </div>
      )}
      <div className="token-usage">
        Visual gate: model {visual.provenance.model_id} · template {visual.provenance.prompt_template_version} · {visual.provenance.timestamp_utc} · tokens{" "}
        {visual.usage.total_tokens} · image calls {visual.image_calls} · outside_box_diff {visual.outside_box_diff ?? "—"}
      </div>
    </div>
  );
}
