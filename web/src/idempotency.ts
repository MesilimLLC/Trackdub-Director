export type IdempotencyScope = "audition" | "pipeline";

const storageKey = (scope: IdempotencyScope) => `trackdub-director.pending-${scope}-idempotency-key`;

export function createIdempotencyKey(): string {
  return `${Date.now()}_${crypto.randomUUID()}`;
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
