import {findNativeBackgroundSequence, nativeFrameIndexAtElapsed} from '../src/bigPicture/nativeBackground';

describe('native shell background playback', () => {
  it('advances forward and wraps without reversing recovered motion', () => {
    expect(nativeFrameIndexAtElapsed(0, 51, 100)).toBe(0);
    expect(nativeFrameIndexAtElapsed(100, 51, 100)).toBe(1);
    expect(nativeFrameIndexAtElapsed(5000, 51, 100)).toBe(50);
    expect(nativeFrameIndexAtElapsed(5100, 51, 100)).toBe(0);
    expect(nativeFrameIndexAtElapsed(5200, 51, 100)).toBe(1);
  });

  it('keeps incomplete or invalid sequences on their first frame', () => {
    expect(nativeFrameIndexAtElapsed(1000, 1, 100)).toBe(0);
    expect(nativeFrameIndexAtElapsed(1000, 51, 0)).toBe(0);
  });
});

describe('native shell background sequence discovery', () => {
  it('sorts recovered frames and derives their authored cadence', async () => {
    const listDirectory = jest.fn().mockResolvedValue([
      {name: 'native-background_0200ms.png', path: 'C:\\oracle\\native-background_0200ms.png', kind: 'file'},
      {name: 'notes.txt', path: 'C:\\oracle\\notes.txt', kind: 'file'},
      {name: 'native-background_0000ms.png', path: 'C:\\oracle\\native-background_0000ms.png', kind: 'file'},
      {name: 'native-background_0100ms.png', path: 'C:\\oracle\\native-background_0100ms.png', kind: 'file'},
    ]);

    await expect(findNativeBackgroundSequence({listDirectory}, ['C:\\oracle'])).resolves.toEqual({
      frames: [
        'C:\\oracle\\native-background_0000ms.png',
        'C:\\oracle\\native-background_0100ms.png',
        'C:\\oracle\\native-background_0200ms.png',
      ],
      frameMs: 100,
      sourceDirectory: 'C:\\oracle',
    });
  });

  it('falls through missing and incomplete recovery stages', async () => {
    const listDirectory = jest.fn()
      .mockRejectedValueOnce(new Error('missing'))
      .mockResolvedValueOnce([{name: 'native-background_0000ms.png', path: 'one.png', kind: 'file'}])
      .mockResolvedValueOnce([
        {name: 'native-background-bottom_0500ms.png', path: 'second.png', kind: 'file'},
        {name: 'native-background-bottom_0000ms.png', path: 'first.png', kind: 'file'},
      ]);

    const sequence = await findNativeBackgroundSequence(
      {listDirectory},
      ['missing', 'incomplete', 'fallback'],
    );
    expect(sequence).toEqual({
      frames: ['first.png', 'second.png'],
      frameMs: 500,
      sourceDirectory: 'fallback',
    });
  });
});
