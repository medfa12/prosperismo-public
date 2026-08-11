import {shellCardMedia} from '../src/bigPicture/shellCardMedia';
import {INITIAL_SHELL_STATE} from '../src/bigPicture/shellState';

const game = {
  backgroundPath: 'D:\\Games\\Astro\\sce_sys\\pic0.png',
  titleMusicPath: 'D:\\Games\\Astro\\sce_sys\\snd0.at9',
} as any;

describe('shell card media ownership', () => {
  it('lets a focused game card replace ambient artwork and music', () => {
    expect(shellCardMedia(game, INITIAL_SHELL_STATE)).toEqual({
      artworkPath: game.backgroundPath,
      musicPath: game.titleMusicPath,
      ownsArtwork: true,
      ownsMusic: true,
    });
  });

  it.each(['spaces', 'library-shortcut', 'system'] as const)(
    'keeps ambient media when the selected title is remembered behind %s focus',
    focusRegion => {
      expect(shellCardMedia(game, {...INITIAL_SHELL_STATE, focusRegion})).toEqual({
        artworkPath: undefined,
        musicPath: undefined,
        ownsArtwork: false,
        ownsMusic: false,
      });
    },
  );

  it('routes artwork and music independently when a title owns only one', () => {
    expect(shellCardMedia({...game, titleMusicPath: undefined}, INITIAL_SHELL_STATE)).toMatchObject({
      artworkPath: game.backgroundPath,
      ownsArtwork: true,
      ownsMusic: false,
    });
    expect(shellCardMedia({...game, backgroundPath: undefined}, INITIAL_SHELL_STATE)).toMatchObject({
      musicPath: game.titleMusicPath,
      ownsArtwork: false,
      ownsMusic: true,
    });
  });

  it('returns ambient ownership for utility surfaces and modals', () => {
    expect(shellCardMedia(game, {...INITIAL_SHELL_STATE, surface: 'settings'})).toMatchObject({
      ownsArtwork: false,
      ownsMusic: false,
    });
    expect(shellCardMedia(game, INITIAL_SHELL_STATE, true)).toMatchObject({
      ownsArtwork: false,
      ownsMusic: false,
    });
  });
});
