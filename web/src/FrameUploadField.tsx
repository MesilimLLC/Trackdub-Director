import type { SegmentInput } from "./api";
import { MAX_IMAGE_BYTES, validateImageMetadata } from "./workbench";

async function sha256Hex(file: File): Promise<string | null> {
  if (!globalThis.crypto?.subtle) return null;
  const digest = await globalThis.crypto.subtle.digest("SHA-256", await file.arrayBuffer());
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

async function readFrameFile(file: File): Promise<{ dataUrl: string; width: number; height: number; sha256: string | null }> {
  const objectUrl = URL.createObjectURL(file);
  try {
    const dims = await new Promise<{ width: number; height: number }>((resolve, reject) => {
      const image = new Image();
      image.onload = () => resolve({ width: image.naturalWidth, height: image.naturalHeight });
      image.onerror = () => reject(new Error("Could not read frame image."));
      image.src = objectUrl;
    });
    const error = validateImageMetadata(file, dims.width, dims.height);
    if (error) throw new Error(error);
    const dataUrl = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result));
      reader.onerror = () => reject(new Error("Could not read frame image."));
      reader.readAsDataURL(file);
    });
    return { dataUrl, ...dims, sha256: await sha256Hex(file) };
  } finally {
    URL.revokeObjectURL(objectUrl);
  }
}

export function FrameUploadField({
  segment,
  busy,
  onFrameLoaded,
  onError,
}: {
  segment: SegmentInput;
  busy: boolean;
  onFrameLoaded: (expected: SegmentInput, patch: Partial<SegmentInput>) => boolean;
  onError: (message: string | null) => void;
}) {
  return (
    <label>Frame image
      <input aria-label="Frame image" type="file" accept="image/png,image/jpeg,image/webp" disabled={busy} onChange={(e) => {
        const file = e.target.files?.[0];
        e.target.value = "";
        if (!file) return;
        if (file.size > MAX_IMAGE_BYTES) { onError("Frame exceeds the 10 MiB limit."); return; }
        const expected = segment;
        void readFrameFile(file).then(({ dataUrl, sha256 }) => {
          const applied = onFrameLoaded(expected, {
            frame_image_data_url: dataUrl,
            frame_id: expected.frame_id ?? file.name,
            frame_sha256: sha256,
            // A sign box is defined against one frame, so a replacement frame
            // must not inherit the old coordinates.
            box: null,
          });
          onError(applied ? null : "Frame upload finished after that segment was replaced. Choose the frame again.");
        }).catch((error: unknown) => onError(error instanceof Error ? error.message : String(error)));
      }} />
    </label>
  );
}
