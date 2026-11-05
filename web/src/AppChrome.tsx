import {
  type QaReport,
  type SegmentInput,
  type StatusResponse,
} from "./api";
import { QaSummary } from "./QaReport";
import { type BusyState } from "./hooks";

export function Brand() {
  return (
    <div className="brand">
      <h1 className="brand-title">Trackdub Director</h1>
      <p className="brand-sub">Meta AI Developer Hackathon — Director Lane</p>
    </div>
  );
}

export function HeaderStatus({ status }: { status: StatusResponse | null }) {
  return (
    <div className="header-meta">
      <div className="pill" title={status?.baseUrl ?? ""}>
        <span className="pill-label">Model</span>
        <span className="pill-value">{status?.model ?? "…"}</span>
      </div>
      <div className={`pill ${status?.hasKey ? "pill-ok" : "pill-warn"}`}>
        <span className="pill-label">Key</span>
        <span className="pill-value">{status ? (status.hasKey ? "configured" : "not set") : "…"}</span>
      </div>
    </div>
  );
}

export function KeyWarning() {
  return (
    <div className="banner banner-warn">
      No Meta API key detected. Set <code>META_API_KEY</code> and restart the server to enable Translate and QA. The UI still loads without a key.
    </div>
  );
}

export function AppHeader({ status, statusError, error, qa }: {
  status: StatusResponse | null;
  statusError: string | null;
  error: string | null;
  qa: QaReport | null;
}) {
  return (
    <header className="app-header">
      <div className="app-header-inner">
        <Brand />
        <HeaderStatus status={status} />
        <QaSummary qa={qa} />
      </div>
      {statusError && <div className="banner banner-error">{statusError}</div>}
      {error && <div className="banner banner-error">{error}</div>}
      {!status?.hasKey && !statusError && <KeyWarning />}
    </header>
  );
}

export interface ToolbarProps {
  segments: SegmentInput[];
  from: string;
  to: string;
  busy: BusyState;
  fileInputRef: React.RefObject<HTMLInputElement | null>;
  onFileChange: (e: React.ChangeEvent<HTMLInputElement>) => void;
  onLoadSample: () => void;
  onTranslate: () => Promise<void>;
  onQa: () => Promise<void>;
  onScene: () => Promise<void>;
  onDirector: () => Promise<void>;
  onAudition: () => Promise<void>;
  auditionRetryPending: boolean;
  onNewAudition: () => void;
  onVisual: () => Promise<void>;
}

type StageButtonSpec = {
  label: string;
  busyLabel: string;
  busyKind: string;
  action: () => Promise<void>;
};

function StageButtons({ buttons, busy, disabled }: { buttons: StageButtonSpec[]; busy: BusyState; disabled: boolean }) {
  return (
    <>
      {buttons.map((button) => (
        <button key={button.busyKind} type="button" className="btn btn-primary" onClick={() => void button.action()} disabled={disabled}>
          {busy === button.busyKind ? button.busyLabel : button.label}
        </button>
      ))}
    </>
  );
}

function AuditionControls(props: ToolbarProps & { disabled: boolean }) {
  const label = props.busy === "audition"
    ? "Running Audition…"
    : props.auditionRetryPending ? "Retry same Audition request" : "Run Audition";
  return (
    <>
      <button type="button" className="btn btn-primary" onClick={() => void props.onAudition()} disabled={props.disabled}>
        {label}
      </button>
      {props.auditionRetryPending && (
        <button type="button" className="btn" disabled={props.disabled} onClick={props.onNewAudition}>Start a new audition</button>
      )}
      {props.auditionRetryPending && (
        <span className="toolbar-hint">A stored key is safe to retry for 20 minutes. After restart, the outcome may be unknown; a new audition can repeat paid calls.</span>
      )}
    </>
  );
}

function FileControls({ fileInputRef, onFileChange, onLoadSample }: Pick<ToolbarProps, "fileInputRef" | "onFileChange" | "onLoadSample">) {
  return (
    <div className="toolbar-right">
      <button type="button" className="btn" onClick={onLoadSample}>
        Load sample
      </button>
      <label className="btn file-btn">
        Load JSON
        <input ref={fileInputRef} type="file" accept=".json,application/json" onChange={onFileChange} hidden />
      </label>
    </div>
  );
}

export function Toolbar(props: ToolbarProps) {
  const disabled = props.busy !== null || props.segments.length === 0;
  const buttons: StageButtonSpec[] = [
    { label: "Run Translate", busyLabel: "Translating…", busyKind: "translate", action: props.onTranslate },
    { label: "Run QA", busyLabel: "Running QA…", busyKind: "qa", action: props.onQa },
    { label: "Run Scene", busyLabel: "Running Scene…", busyKind: "scene", action: props.onScene },
    { label: "Run Director", busyLabel: "Running Director…", busyKind: "director", action: props.onDirector },
  ];
  return (
    <div className="toolbar">
      <div className="toolbar-left">
        <StageButtons buttons={buttons} busy={props.busy} disabled={disabled} />
        <AuditionControls {...props} disabled={disabled} />
        <button type="button" className="btn btn-primary" onClick={() => void props.onVisual()} disabled={disabled}>
          {props.busy === "visual" ? "Running Visual…" : "Run Visual"}
        </button>
        <span className="toolbar-hint">
          {props.segments.length} segments · {props.from} → {props.to}
        </span>
      </div>
      <FileControls {...props} />
    </div>
  );
}

export function AppFooter() {
  return (
    <footer className="app-footer">
      <span>Trackdub Director · Core, Server, web frontend, CLI harness</span>
      <span className="footer-sep">·</span>
      <a href="/api/status">/api/status</a>
    </footer>
  );
}
