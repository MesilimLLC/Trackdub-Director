type WavInfo = {
  format: number;
  channels: number;
  sampleRate: number;
  bitsPerSample: number;
  blockAlign: number;
  dataBytes: number;
  hasAudioData: boolean;
};

const SUPPORTED_SAMPLE_RATES = new Set([16000, 24000]);

type PartialWavInfo = Partial<WavInfo> & { dataBytes: number };

function readTag(buffer: ArrayBuffer, offset: number): string {
  return String.fromCharCode(...new Uint8Array(buffer, offset, 4));
}

function assertWavHeader(buffer: ArrayBuffer): void {
  if (buffer.byteLength < 12) throw new Error("The selected file is not a complete WAV file.");
  if (!hasRiffWaveTags(buffer)) {
    throw new Error("Choose a RIFF/WAVE audio file.");
  }
}

function hasRiffWaveTags(buffer: ArrayBuffer): boolean {
  return readTag(buffer, 0) === "RIFF" && readTag(buffer, 8) === "WAVE";
}

function assertValidRiffEnd(riffEnd: number, byteLength: number): void {
  if (riffEnd < 12 || riffEnd > byteLength) throw new Error("The WAV file is truncated or has an invalid RIFF size.");
}

function readRiffEnd(buffer: ArrayBuffer, view: DataView): number {
  assertWavHeader(buffer);
  const riffEnd = view.getUint32(4, true) + 8;
  assertValidRiffEnd(riffEnd, buffer.byteLength);
  return riffEnd;
}

type ChunkHeader = {
  tag: string;
  size: number;
  dataOffset: number;
  nextOffset: number;
};

function readChunkHeader(buffer: ArrayBuffer, view: DataView, offset: number, riffEnd: number): ChunkHeader {
  const size = view.getUint32(offset + 4, true);
  const dataOffset = offset + 8;
  const chunkEnd = dataOffset + size;
  if (chunkEnd > riffEnd) throw new Error("The WAV contains a truncated audio chunk.");
  const nextOffset = chunkEnd + (size & 1);
  if (nextOffset > riffEnd) throw new Error("The WAV is missing a chunk padding byte.");
  return { tag: readTag(buffer, offset), size, dataOffset, nextOffset };
}

function readFormatChunk(view: DataView, header: ChunkHeader, info: PartialWavInfo): void {
  if (header.size < 16) throw new Error("The WAV format header is incomplete.");
  info.format = view.getUint16(header.dataOffset, true);
  info.channels = view.getUint16(header.dataOffset + 2, true);
  info.sampleRate = view.getUint32(header.dataOffset + 4, true);
  info.blockAlign = view.getUint16(header.dataOffset + 12, true);
  info.bitsPerSample = view.getUint16(header.dataOffset + 14, true);
}

function scanChunks(buffer: ArrayBuffer, view: DataView, riffEnd: number): PartialWavInfo {
  const info: PartialWavInfo = { dataBytes: 0 };
  for (let offset = 12; offset + 8 <= riffEnd;) {
    const header = readChunkHeader(buffer, view, offset, riffEnd);
    if (header.tag === "fmt ") readFormatChunk(view, header, info);
    else if (header.tag === "data") info.dataBytes += header.size;
    offset = header.nextOffset;
  }
  return info;
}

function isCompleteInfo(info: PartialWavInfo): info is WavInfo {
  return info.format !== undefined
    && info.channels !== undefined
    && info.sampleRate !== undefined
    && info.bitsPerSample !== undefined
    && info.blockAlign !== undefined
    && info.dataBytes > 0;
}

function validatePcmLayout(info: WavInfo): void {
  if (info.format !== 1) return;
  assertPcmBlockLayout(info);
  assertPcmDataAlignment(info);
}

function assertPcmBlockLayout(info: WavInfo): void {
  if (info.channels === 0) throw new Error("The PCM WAV data does not match its format header.");
  if (info.bitsPerSample === 0) throw new Error("The PCM WAV data does not match its format header.");
  const frameBytes = info.channels * Math.ceil(info.bitsPerSample / 8);
  if (info.blockAlign !== frameBytes) throw new Error("The PCM WAV data does not match its format header.");
}

function assertPcmDataAlignment(info: WavInfo): void {
  if (info.blockAlign === 0) throw new Error("The PCM WAV data does not match its format header.");
  if (info.dataBytes % info.blockAlign !== 0) throw new Error("The PCM WAV data does not match its format header.");
}

export function inspectWav(buffer: ArrayBuffer): WavInfo {
  const view = new DataView(buffer);
  const riffEnd = readRiffEnd(buffer, view);
  const scanned = scanChunks(buffer, view, riffEnd);
  if (!isCompleteInfo(scanned)) {
    throw new Error("The WAV must contain a format header and non-empty audio data.");
  }
  validatePcmLayout(scanned);
  return { ...scanned, hasAudioData: scanned.dataBytes > 0 };
}

function isSupported(info: WavInfo): boolean {
  return info.format === 1
    && info.channels === 1
    && info.bitsPerSample === 16
    && info.blockAlign === 2
    && SUPPORTED_SAMPLE_RATES.has(info.sampleRate);
}

function assertConversionAvailable(): void {
  if (typeof AudioContext === "undefined" || typeof OfflineAudioContext === "undefined") {
    throw new Error("This WAV needs conversion to mono 16-bit PCM at 16 or 24 kHz, but audio conversion is unavailable in this browser.");
  }
}

async function renderMono(decoded: AudioBuffer, targetRate: number): Promise<AudioBuffer> {
  const frameCount = Math.max(1, Math.ceil(decoded.duration * targetRate));
  const offline = new OfflineAudioContext(1, frameCount, targetRate);
  const sourceNode = offline.createBufferSource();
  sourceNode.buffer = decoded;
  sourceNode.connect(offline.destination);
  sourceNode.start();
  return offline.startRendering();
}

async function convertWav(file: File, source: ArrayBuffer): Promise<File> {
  let decoder: AudioContext | undefined;
  try {
    decoder = new AudioContext();
    const decoded = await decoder.decodeAudioData(source.slice(0));
    const targetRate = 16000;
    const converted = await renderMono(decoded, targetRate);
    const wav = encodeMonoPcm16(converted.getChannelData(0), targetRate);
    return new File([wav], file.name || "audio.wav", { type: "audio/wav", lastModified: file.lastModified });
  } catch {
    throw new Error("This WAV could not be decoded for conversion. Choose a valid PCM WAV and try again.");
  } finally {
    await decoder?.close().catch(() => undefined);
  }
}

export async function normalizeWavForTranscription(file: File): Promise<File> {
  const source = await readArrayBuffer(file);
  if (isSupported(inspectWav(source))) return file;
  assertConversionAvailable();
  return convertWav(file, source);
}

function readArrayBuffer(blob: Blob): Promise<ArrayBuffer> {
  if (typeof blob.arrayBuffer === "function") return blob.arrayBuffer();
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(reader.error ?? new Error("Unable to read the selected WAV file."));
    reader.onload = () => {
      if (reader.result instanceof ArrayBuffer) resolve(reader.result);
      else reject(new Error("Unable to read the selected WAV file."));
    };
    reader.readAsArrayBuffer(blob);
  });
}

function encodeMonoPcm16(samples: Float32Array, sampleRate: number): ArrayBuffer {
  const dataBytes = samples.length * 2;
  const buffer = new ArrayBuffer(44 + dataBytes);
  const view = new DataView(buffer);
  const writeTag = (offset: number, tag: string) => {
    for (let i = 0; i < tag.length; i++) view.setUint8(offset + i, tag.charCodeAt(i));
  };
  writeTag(0, "RIFF");
  view.setUint32(4, 36 + dataBytes, true);
  writeTag(8, "WAVE");
  writeTag(12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  writeTag(36, "data");
  view.setUint32(40, dataBytes, true);
  for (let i = 0; i < samples.length; i++) {
    const sample = Math.max(-1, Math.min(1, samples[i]));
    view.setInt16(44 + i * 2, sample < 0 ? sample * 32768 : sample * 32767, true);
  }
  return buffer;
}
