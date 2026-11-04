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
  onVisual: () => Promise<void>;
}

export function Toolbar({ segments, from, to, busy, fileInputRef, onFileChange, onLoadSample, onTranslate, onQa, onScene, onDirector, onAudition, onVisual }: ToolbarProps) {
  const disabled = busy !== null || segments.length === 0;
  return (
    <div className="toolbar">
      <div className="toolbar-left">
        <button type="button" className="btn btn-primary" onClick={() => void onTranslate()} disabled={disabled}>
          {busy === "translate" ? "Translating…" : "Run Translate"}
        </button>
        <button type="button" className="btn btn-primary" onClick={() => void onQa()} disabled={disabled}>
          {busy === "qa" ? "Running QA…" : "Run QA"}
        </button>
        <button type="button" className="btn btn-primary" onClick={() => void onScene()} disabled={disabled}>
          {busy === "scene" ? "Running Scene…" : "Run Scene"}
        </button>
        <button type="button" className="btn btn-primary" onClick={() => void onDirector()} disabled={disabled}>
          {busy === "director" ? "Running Director…" : "Run Director"}
        </button>
        <button type="button" className="btn btn-primary" onClick={() => void onAudition()} disabled={disabled}>
          {busy === "audition" ? "Running Audition…" : "Run Audition"}
        </button>
        <button type="button" className="btn btn-primary" onClick={() => void onVisual()} disabled={disabled}>
          {busy === "visual" ? "Running Visual…" : "Run Visual"}
        </button>
        <span className="toolbar-hint">
          {segments.length} segments · {from} → {to}
        </span>
      </div>
      <div className="toolbar-right">
        <button type="button" className="btn" onClick={onLoadSample}>
          Load sample
        </button>
        <label className="btn file-btn">
          Load JSON
          <input ref={fileInputRef} type="file" accept=".json,application/json" onChange={onFileChange} hidden />
        </label>
      </div>
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
