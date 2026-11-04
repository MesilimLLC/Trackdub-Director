import { describe, expect, it } from "vitest";
import type { AuditionReport, SegmentInput } from "./api";
import {
  buildDubExportPlacements,
  classifySegmentChanges,
  normalizeBox,
  pointerToOriginal,
  reportsForChanges,
  stripAudioTransport,
  validateImageMetadata,
  wavDataUrlToBlob,
} from "./workbench";

const base: SegmentInput = {
  id: "seg-1",
  text: "Hello",
  duration_seconds: 1,
  speaker_id: "anna",
  frame_image_data_url: "data:image/png;base64,AA==",
  concepts: ["sign"],
  box: { x1: 10, y1: 10, x2: 50, y2: 40 },
  source_text: "SHOP",
  target_text: "TIENDA",
};

describe("workbench invalidation", () => {
  it("keeps QA evidence when only Director register changes", () => {
    const next = [{ ...base, director_adaptation: { target_register: "formal" } }];
    const changes = classifySegmentChanges([base], next);
    expect(changes).toEqual(["director"]);
    expect(reportsForChanges(changes)).toEqual(["director"]);
  });

  it("keeps the frame and Visual evidence when only concepts change", () => {
    const next = [{ ...base, concepts: ["door"] }];
    const changes = classifySegmentChanges([base], next);
    expect(changes).toEqual(["sceneConfig"]);
    expect(reportsForChanges(changes)).not.toContain("visual");
    expect(next[0].frame_image_data_url).toBe(base.frame_image_data_url);
  });

  it("clears text-dependent evidence but preserves Scene and Visual on text edits", () => {
    const reports = reportsForChanges(classifySegmentChanges([base], [{ ...base, text: "Changed" }]));
    expect(reports).toEqual(expect.arrayContaining(["batch", "qa", "director", "audition", "pipeline"]));
    expect(reports).not.toContain("scene");
    expect(reports).not.toContain("visual");
  });
});

describe("visual geometry", () => {
  it("converts a 50% preview point to original pixels", () => {
    expect(pointerToOriginal(110, 70, { left: 10, top: 20, width: 960, height: 540 }, { width: 1920, height: 1080 }))
      .toEqual({ x: 200, y: 100 });
  });

  it("normalizes reverse drags, clamps bounds, and rejects zero-area boxes", () => {
    expect(normalizeBox({ x: 2500, y: 900 }, { x: 100, y: 100 }, { width: 1920, height: 1080 }))
      .toEqual({ x1: 100, y1: 100, x2: 1920, y2: 900 });
    expect(normalizeBox({ x: 100, y: 100 }, { x: 104, y: 104 }, { width: 1920, height: 1080 })).toBeNull();
  });
});

describe("visual upload limits", () => {
  it("accepts the server maximum frame and rejects unsupported or oversized frames", () => {
    expect(validateImageMetadata({ size: 1024, type: "image/png" }, 4096, 4096)).toBeNull();
    expect(validateImageMetadata({ size: 1024, type: "image/gif" }, 640, 480)).toContain("PNG, JPEG, or WebP");
    expect(validateImageMetadata({ size: 10 * 1024 * 1024 + 1, type: "image/png" }, 640, 480)).toContain("10 MiB");
    expect(validateImageMetadata({ size: 1024, type: "image/png" }, 4097, 480)).toContain("4096");
  });
});

describe("dub export timing", () => {
  it("keeps existing stage evidence when only the export start changes", () => {
    const changes = classifySegmentChanges([base], [{ ...base, start_seconds: 1.25 }]);
    expect(changes).toEqual(["start"]);
    expect(reportsForChanges(changes)).toEqual([]);
  });
});

describe("dub export placements", () => {
  const audition = {
    segments: [{
      id: "seg-1",
      text: "Hello",
      window_seconds: 1,
      cast: true,
      winner_take_number: 2,
      winner_file_name: "seg-1-take2.wav",
      takes: [{
        take_number: 2,
        kind: "Initial",
        text: "Hola",
        meaning_verdict: "Pass",
        meaning_issues: [],
        meaning_explanation: null,
        synthesized: true,
        window_seconds: 1,
        timing_verdict: "Pass",
        is_winner: true,
        audio_file_name: "seg-1-take2.wav",
        translation_tokens: 0,
        meaning_tokens: 0,
        elevenlabs_characters: 4,
        audio_data_url: "data:audio/wav;base64,UklGRg==",
      }],
    }],
  } as unknown as AuditionReport;

  it("requires an explicit start and a transported winner for every segment", async () => {
    const missingStart = buildDubExportPlacements([base], audition);
    expect(missingStart.problems).toContain("seg-1 needs a start time.");
    expect(missingStart.placements).toEqual([]);

    const ready = buildDubExportPlacements([{ ...base, start_seconds: 0.25 }], audition);
    expect(ready.problems).toEqual([]);
    expect(ready.placements).toMatchObject([{ id: "seg-1", start_seconds: 0.25, file_name: "seg-1-take2.wav" }]);

    const blob = wavDataUrlToBlob(ready.placements[0].audio_data_url);
    expect(blob.type).toBe("audio/wav");
    expect(blob.size).toBe(4);
  });
});

describe("evidence export", () => {
  it("recursively removes audio transport data URLs", () => {
    const evidence = stripAudioTransport({
      pipeline: { audition: { segments: [{ takes: [{ audio_data_url: "data:audio/wav;base64,AA==", audio_file_name: "take.wav" }] }] } },
    });
    expect(JSON.stringify(evidence)).not.toContain("audio_data_url");
    expect(JSON.stringify(evidence)).toContain("take.wav");
  });
});
