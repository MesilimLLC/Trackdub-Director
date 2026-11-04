import {
  type SegmentInput,
} from "./api";

export function pct(n: number): string {
  return `${Math.round(n * 100)}%`;
}

export function fmtSec(n: number): string {
  return `${n.toFixed(2)}s`;
}

export function verdictClass(verdict: string): string {
  return `verdict verdict-${verdict.toLowerCase()}`;
}

export function errorMessage(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

export function orDash(value: string | null | undefined): string {
  return value ?? "—";
}

export function listOrDash(values: string[] | null | undefined): string {
  return values?.length ? values.join(" | ") : "—";
}

export function parseSegments(text: string): SegmentInput[] {
  const parsed = JSON.parse(text) as unknown;
  if (!Array.isArray(parsed)) {
    throw new Error("JSON must be an array of { id, text, duration_seconds? }");
  }

  return parsed.map((raw, index) => {
    const segment = raw as SegmentInput & { speaker?: string | null };
    if (!segment?.id || typeof segment.text !== "string") {
      throw new Error(`Segment at index ${index} must have { id, text }`);
    }
    // Ingest writes diarization under "speaker"; the web contract uses speaker_id.
    const speakerId = segment.speaker_id ?? segment.speaker ?? null;
    return { ...segment, speaker_id: speakerId };
  });
}
