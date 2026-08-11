import type {DirectoryEntry, ProsperismoHostGateway} from '../core/host';

export interface NativeBackgroundSequence {
  frames: string[];
  frameMs: number;
  sourceDirectory: string;
}

type DirectoryReader = Pick<ProsperismoHostGateway, 'listDirectory'>;

const TIMED_FRAME = /_(\d+)ms\.png$/i;

function timedFrames(entries: readonly DirectoryEntry[]): {path: string; atMs: number}[] {
  return entries
    .filter(entry => entry.kind === 'file')
    .map(entry => {
      const match = TIMED_FRAME.exec(entry.name);
      return match ? {path: entry.path, atMs: Number(match[1])} : undefined;
    })
    .filter((entry): entry is {path: string; atMs: number} => entry !== undefined && Number.isFinite(entry.atMs))
    .sort((left, right) => left.atMs - right.atMs);
}

function recoveredCadence(frames: readonly {atMs: number}[]): number {
  const deltas = frames.slice(1)
    .map((frame, index) => frame.atMs - frames[index].atMs)
    .filter(delta => delta > 0);
  return Math.max(16, Math.min(1000, deltas.length > 0 ? Math.min(...deltas) : 100));
}

/**
 * The recovered cache advances in ordinal order and wraps to its first frame.
 * It never reverses the firmware-authored motion at the end of a capture.
 */
export function nativeFrameIndexAtElapsed(
  elapsedMs: number,
  frameCount: number,
  frameMs: number,
): number {
  if (frameCount <= 1 || frameMs <= 0 || elapsedMs <= 0) {
    return 0;
  }
  return Math.floor(elapsedMs / frameMs) % frameCount;
}

/**
 * Finds the first complete renderer-output sequence. Directory discovery keeps
 * the RN bridge compatible with longer shell-shot captures without baking a
 * fixed frame count or proprietary files into the application.
 */
export async function findNativeBackgroundSequence(
  host: DirectoryReader,
  candidateDirectories: readonly string[],
): Promise<NativeBackgroundSequence | undefined> {
  for (const sourceDirectory of candidateDirectories) {
    try {
      const frames = timedFrames(await host.listDirectory(sourceDirectory));
      if (frames.length >= 2) {
        return {
          frames: frames.map(frame => frame.path),
          frameMs: recoveredCadence(frames),
          sourceDirectory,
        };
      }
    } catch {
      // A user's oracle is optional and may contain only some recovery stages.
    }
  }
  return undefined;
}
