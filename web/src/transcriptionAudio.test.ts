import { afterEach, describe, expect, it, vi } from "vitest";
import { inspectWav, normalizeWavForTranscription } from "./transcriptionAudio";

function readFile(file: Blob): Promise<ArrayBuffer> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(reader.error);
    reader.onload = () => reader.result instanceof ArrayBuffer ? resolve(reader.result) : reject(new Error("not an ArrayBuffer"));
    reader.readAsArrayBuffer(file);
  });
}

function makeWav(sampleRate = 16000, channels = 1, bitsPerSample = 16): ArrayBuffer {
  const bytesPerSample = bitsPerSample / 8;
  const dataSize = channels * bytesPerSample;
  const buffer = new ArrayBuffer(44 + dataSize + (dataSize & 1));
  const view = new DataView(buffer);
  const tag = (offset: number, value: string) => [...value].forEach((char, index) => view.setUint8(offset + index, char.charCodeAt(0)));
  tag(0, "RIFF"); view.setUint32(4, buffer.byteLength - 8, true); tag(8, "WAVE"); tag(12, "fmt ");
  view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, channels, true);
  view.setUint32(24, sampleRate, true); view.setUint32(28, sampleRate * channels * bytesPerSample, true);
  view.setUint16(32, channels * bytesPerSample, true); view.setUint16(34, bitsPerSample, true);
  tag(36, "data"); view.setUint32(40, dataSize, true);
  return buffer;
}

describe("transcription audio", () => {
  afterEach(() => vi.unstubAllGlobals());
  it("recognizes the exact WAV encoding accepted by Meta ASR", () => {
    expect(inspectWav(makeWav())).toMatchObject({ format: 1, channels: 1, sampleRate: 16000, bitsPerSample: 16, hasAudioData: true });
    expect(inspectWav(makeWav(24000))).toMatchObject({ sampleRate: 24000 });
  });

  it("converts a valid stereo 48 kHz WAV to mono 16-bit 16 kHz before upload", async () => {
    const decoded = {
      duration: 0.000125,
      numberOfChannels: 2,
      sampleRate: 48000,
    } as AudioBuffer;
    class MockAudioContext {
      decodeAudioData = vi.fn(async () => decoded);
      close = vi.fn(async () => undefined);
    }
    class MockOfflineAudioContext {
      destination = {} as AudioDestinationNode;
      createBufferSource() {
        return { buffer: null as AudioBuffer | null, connect: vi.fn(), start: vi.fn() };
      }
      async startRendering() {
        return { getChannelData: () => new Float32Array([-0.5, 0.5]) } as unknown as AudioBuffer;
      }
    }
    vi.stubGlobal("AudioContext", MockAudioContext);
    vi.stubGlobal("OfflineAudioContext", MockOfflineAudioContext);

    const file = new File([makeWav(48000, 2)], "stereo.wav", { type: "audio/wav" });
    const normalized = await normalizeWavForTranscription(file);

    expect(normalized).not.toBe(file);
    expect(normalized.name).toBe("stereo.wav");
    expect(inspectWav(await readFile(normalized))).toMatchObject({ format: 1, channels: 1, sampleRate: 16000, bitsPerSample: 16 });
  });

  it("converts a valid mono 24-bit 44.1 kHz WAV before upload", async () => {
    const decoded = { duration: 0.000125 } as AudioBuffer;
    class MockAudioContext {
      decodeAudioData = vi.fn(async () => decoded);
      close = vi.fn(async () => undefined);
    }
    class MockOfflineAudioContext {
      destination = {} as AudioDestinationNode;
      createBufferSource() {
        return { buffer: null as AudioBuffer | null, connect: vi.fn(), start: vi.fn() };
      }
      async startRendering() {
        return { getChannelData: () => new Float32Array([0.25, -0.25]) } as unknown as AudioBuffer;
      }
    }
    vi.stubGlobal("AudioContext", MockAudioContext);
    vi.stubGlobal("OfflineAudioContext", MockOfflineAudioContext);

    const file = new File([makeWav(44100, 1, 24)], "mono-24bit.wav", { type: "audio/wav" });
    const normalized = await normalizeWavForTranscription(file);

    expect(normalized).not.toBe(file);
    expect(inspectWav(await readFile(normalized))).toMatchObject({ format: 1, channels: 1, sampleRate: 16000, bitsPerSample: 16 });
  });

  it("rejects malformed containers before attempting conversion", async () => {
    const invalid = new File([new Uint8Array([82, 73, 70, 70])], "broken.wav", { type: "audio/wav" });
    await expect(normalizeWavForTranscription(invalid)).rejects.toThrow("complete WAV file");
  });
});
