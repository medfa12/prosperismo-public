export const SHELL_SEQUENCE_FPS = 30;
export const SHELL_SEQUENCE_FRAME_COUNT = 540;
export const SHELL_AMBIENT_START_FRAME = 330;
export const SHELL_COLD_BOOT_DURATION_MS =
  SHELL_AMBIENT_START_FRAME / SHELL_SEQUENCE_FPS * 1000;

/** Mirrors the native WIC frame owner for deterministic boundary tests. */
export function shellSequenceFrame(elapsedMs: number, coldBootActive: boolean): number {
  const elapsedFrames = Math.max(0, Math.floor(elapsedMs * SHELL_SEQUENCE_FPS / 1000));
  if (coldBootActive) {
    return Math.min(SHELL_AMBIENT_START_FRAME - 1, elapsedFrames);
  }
  const ambientFrames = SHELL_SEQUENCE_FRAME_COUNT - SHELL_AMBIENT_START_FRAME;
  return SHELL_AMBIENT_START_FRAME + elapsedFrames % ambientFrames;
}
