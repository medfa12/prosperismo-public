import type {GameInstall} from '../core/models';
import type {ShellState} from './shellState';

export interface ShellCardMedia {
  /** A title plate replaces the ambient background only while its game card owns focus. */
  artworkPath?: string;
  /** A title preview replaces the shell bed independently of title artwork availability. */
  musicPath?: string;
  ownsArtwork: boolean;
  ownsMusic: boolean;
}

/**
 * HOME remembers the selected title while focus moves through generic cards,
 * but those cards do not inherit that title's media. Only the focused game
 * card may replace the persistent ambient room with pic0/snd0.
 */
export function shellCardMedia(
  game: GameInstall | undefined,
  state: ShellState,
  modalOpen = false,
): ShellCardMedia {
  const titleOwnsFocus = Boolean(game) &&
    state.surface === 'home' &&
    state.focusRegion === 'strand' &&
    state.verticalPosition === 'home' &&
    !modalOpen;
  const artworkPath = titleOwnsFocus ? game?.backgroundPath : undefined;
  const musicPath = titleOwnsFocus ? game?.titleMusicPath : undefined;
  return {
    artworkPath,
    musicPath,
    ownsArtwork: Boolean(artworkPath),
    ownsMusic: Boolean(musicPath),
  };
}
