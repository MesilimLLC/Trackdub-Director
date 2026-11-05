export type IdempotencyScope = "audition" | "pipeline";

const storageKey = (scope: IdempotencyScope) => `trackdub-director.pending-${scope}-idempotency-key`;

function randomUuid(): string {
  if (typeof crypto.randomUUID === "function") return crypto.randomUUID();
  // crypto.randomUUID is only exposed in secure contexts. The Windows-hosted
  // workbench can be reached over plain HTTP on a LAN address, so fall back to
  // getRandomValues and build the Guid "D" format the server requires.
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = [...bytes].map((byte) => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export function createIdempotencyKey(): string {
  return `${Date.now()}_${randomUuid()}`;
}

export function readPendingIdempotencyKey(scope: IdempotencyScope): string | null {
  try {
    return localStorage.getItem(storageKey(scope));
  } catch {
    return null;
  }
}

export function savePendingIdempotencyKey(scope: IdempotencyScope, key: string): boolean {
  try {
    localStorage.setItem(storageKey(scope), key);
    return true;
  } catch {
    return false;
  }
}

export function clearPendingIdempotencyKey(scope: IdempotencyScope): void {
  try {
    localStorage.removeItem(storageKey(scope));
  } catch {
    // Leaving a stale key is safe: the server will replay or reject it, never rerun it.
  }
}
