import {
  SHELL_COLD_BOOT_DURATION_MS,
  shellSequenceFrame,
} from '../src/bigPicture/shellBackgroundSequence';

describe('cold-boot and ambient shader sequence', () => {
  it('holds the final cold-boot frame until the UI switches ownership', () => {
    expect(SHELL_COLD_BOOT_DURATION_MS).toBe(11000);
    expect(shellSequenceFrame(0, true)).toBe(0);
    expect(shellSequenceFrame(10999, true)).toBe(329);
    expect(shellSequenceFrame(60000, true)).toBe(329);
  });

  it('loops only the authored ambient continuation', () => {
    expect(shellSequenceFrame(0, false)).toBe(330);
    expect(shellSequenceFrame(6966, false)).toBe(538);
    expect(shellSequenceFrame(7000, false)).toBe(330);
  });
});
