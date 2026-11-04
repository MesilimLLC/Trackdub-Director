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

export function inspectWav(buffer: ArrayBuffer): WavInfo {
  if (buffer.byteLength < 12) throw new Error("The selected file is not a complete WAV file.");
  const view = new DataView(buffer);
  const readTag = (offset: number) => String.fromCharCode(...new Uint8Array(buffer, offset, 4));
  if (readTag(0) !== "RIFF" || readTag(8) !== "WAVE") {
    throw new Error("Choose a RIFF/WAVE audio file.");
  }

  const riffEnd = view.getUint32(4, true) + 8;
  if (riffEnd < 12 || riffEnd > buffer.byteLength) throw new Error("The WAV file is truncated or has an invalid RIFF size.");
  let format: number | undefined;
  let channels: number | undefined;
  let sampleRate: number | undefined;
  let bitsPerSample: number | undefined;
  let blockAlign: number | undefined;
  let dataBytes = 0;
  for (let offset = 12; offset + 8 <= riffEnd;) {
    const size = view.getUint32(offset + 4, true);
    const dataOffset = offset + 8;
    const chunkEnd = dataOffset + size;
    if (chunkEnd > riffEnd) throw new Error("The WAV contains a truncated audio chunk.");
    const tag = readTag(offset);
    if (tag === "fmt ") {
      if (size < 16) throw new Error("The WAV format header is incomplete.");
      format = view.getUint16(dataOffset, true);
      channels = view.getUint16(dataOffset + 2, true);
      sampleRate = view.getUint32(dataOffset + 4, true);
      blockAlign = view.getUint16(dataOffset + 12, true);
      bitsPerSample = view.getUint16(dataOffset + 14, true);
    } else if (tag === "data") {
      dataBytes += size;
    }
    offset = chunkEnd + (size & 1);
    if (offset > riffEnd) throw new Error("The WAV is missing a chunk padding byte.");
  }

  if (format === undefined || channels === undefined || sampleRate === undefined || bitsPerSample === undefined || blockAlign === undefined || dataBytes === 0) {
    throw new Error("The WAV must contain a format header and non-empty audio data.");
  }
  if (format === 1 && (channels === 0 || bitsPerSample === 0 || blockAlign !== channels * Math.ceil(bitsPerSample / 8) || dataBytes % blockAlign !== 0)) {
    throw new Error("The PCM WAV data does not match its format header.");
  }
  return { format, channels, sampleRate, bitsPerSample, blockAlign, dataBytes, hasAudioData: dataBytes > 0 };
}

export async function normalizeWavForTranscription(file: File): Promise<File> {
  const source = await readArrayBuffer(file);
  const info = inspectWav(source);
  if (info.format === 1 && info.channels === 1 && info.bitsPerSample === 16 && info.blockAlign === 2 && SUPPORTED_SAMPLE_RATES.has(info.sampleRate)) {
    return file;
  }

  if (typeof AudioContext === "undefined" || typeof OfflineAudioContext === "undefined") {
    throw new Error("This WAV needs conversion to mono 16-bit PCM at 16 or 24 kHz, but audio conversion is unavailable in this browser.");
  }

  let decoder: AudioContext | undefined;
  try {
    decoder = new AudioContext();
    const decoded = await decoder.decodeAudioData(source.slice(0));
    const targetRate = 16000;
    const frameCount = Math.max(1, Math.ceil(decoded.duration * targetRate));
    const offline = new OfflineAudioContext(1, frameCount, targetRate);
    const sourceNode = offline.createBufferSource();
    sourceNode.buffer = decoded;
    sourceNode.connect(offline.destination);
    sourceNode.start();
    const converted = await offline.startRendering();
    const wav = encodeMonoPcm16(converted.getChannelData(0), targetRate);
    return new File([wav], file.name || "audio.wav", { type: "audio/wav", lastModified: file.lastModified });
  } catch {
    throw new Error("This WAV could not be decoded for conversion. Choose a valid PCM WAV and try again.");
  } finally {
    await decoder?.close().catch(() => undefined);
  }
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
