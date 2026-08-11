import type {GameInstall} from '../core/models';

export type ShellSpace = 'games' | 'media';
export type ShellSurface = 'home' | 'library' | 'settings';
export type ShellFocusRegion = 'spaces' | 'strand' | 'library-shortcut' | 'system' | 'content';
export type ShellDirection = 'left' | 'right' | 'up' | 'down';
/** NPXS40002 m130 recoil atom `verticalPosition`, default "home". */
export type ShellVerticalPosition = 'home' | 'hub';

export interface ShellState {
  space: ShellSpace;
  spaceCursor: ShellSpace;
  surface: ShellSurface;
  focusRegion: ShellFocusRegion;
  verticalPosition: ShellVerticalPosition;
  selectedIndex: number;
  settingsIndex: number;
  systemIndex: number;
}

export type ShellAction =
  | {type: 'focus'; region: ShellFocusRegion}
  | {type: 'select-game'; index: number; gameCount: number}
  | {type: 'move'; delta: -1 | 1; gameCount: number}
  | {type: 'open-library'}
  | {type: 'open-settings'}
  | {type: 'home'}
  | {type: 'focus-space'; space: ShellSpace}
  | {type: 'set-space'; space: ShellSpace}
  | {type: 'select-setting'; index: number}
  | {type: 'select-system'; index: number}
  | {type: 'navigate-home'; direction: ShellDirection; gameCount: number; systemCount: number}
  | {type: 'descend-hub'; hubReady: boolean}
  | {type: 'ascend-home'};

export const INITIAL_SHELL_STATE: ShellState = {
  space: 'games',
  spaceCursor: 'games',
  surface: 'home',
  focusRegion: 'strand',
  verticalPosition: 'home',
  selectedIndex: 0,
  settingsIndex: 0,
  systemIndex: 0,
};

function clamp(value: number, length: number): number {
  return Math.max(0, Math.min(value, Math.max(0, length - 1)));
}

/**
 * Firmware HOME uses named region neighbours rather than a wrapping list:
 * experience -> space switcher -> system actions. Each region remembers its
 * last item, and unavailable directions clamp in place.
 */
export function navigateHomeFocus(
  state: ShellState,
  direction: ShellDirection,
  gameCount: number,
  systemCount: number,
): ShellState {
  if (state.surface !== 'home') {
    return state;
  }
  if (state.focusRegion === 'strand') {
    if (direction === 'left' || direction === 'right') {
      const delta = direction === 'left' ? -1 : 1;
      if (direction === 'right' && gameCount > 0 && state.selectedIndex >= gameCount - 1) {
        return {...state, focusRegion: 'library-shortcut'};
      }
      return {...state, selectedIndex: clamp(state.selectedIndex + delta, gameCount)};
    }
    return direction === 'up' ? {...state, focusRegion: 'spaces'} : state;
  }
  if (state.focusRegion === 'library-shortcut') {
    if (direction === 'left' && gameCount > 0) {
      return {...state, focusRegion: 'strand', selectedIndex: gameCount - 1};
    }
    return direction === 'up' ? {...state, focusRegion: 'spaces'} : state;
  }
  if (state.focusRegion === 'spaces') {
    if (direction === 'left') {
      return state.spaceCursor === 'media' ? {...state, spaceCursor: 'games'} : state;
    }
    if (direction === 'right') {
      return state.spaceCursor === 'games'
        ? {...state, spaceCursor: 'media'}
        : systemCount > 0
          ? {...state, focusRegion: 'system', systemIndex: clamp(state.systemIndex, systemCount)}
          : state;
    }
    return direction === 'down' && gameCount > 0 ? {...state, focusRegion: 'strand'} : state;
  }
  if (state.focusRegion === 'system') {
    if (direction === 'left') {
      return state.systemIndex > 0
        ? {...state, systemIndex: clamp(state.systemIndex - 1, systemCount)}
        : {...state, focusRegion: 'spaces'};
    }
    if (direction === 'right') {
      return {...state, systemIndex: clamp(state.systemIndex + 1, systemCount)};
    }
    return direction === 'down' && gameCount > 0 ? {...state, focusRegion: 'strand'} : state;
  }
  return state;
}

export function reduceShellState(state: ShellState, action: ShellAction): ShellState {
  switch (action.type) {
    case 'focus': return {...state, focusRegion: action.region};
    case 'select-game': return {...state, focusRegion: 'strand', selectedIndex: clamp(action.index, action.gameCount)};
    case 'move': return {...state, selectedIndex: clamp(state.selectedIndex + action.delta, action.gameCount)};
    case 'open-library': return {...state, surface: 'library', focusRegion: 'content'};
    case 'open-settings': return {...state, surface: 'settings', focusRegion: 'content'};
    case 'home': return {...state, surface: 'home', focusRegion: 'strand', verticalPosition: 'home'};
    case 'focus-space': return {...state, spaceCursor: action.space, focusRegion: 'spaces'};
    case 'set-space': return {...state, space: action.space, spaceCursor: action.space, focusRegion: 'spaces'};
    case 'select-setting': return {...state, settingsIndex: Math.max(0, action.index), focusRegion: 'content'};
    case 'select-system': return {...state, systemIndex: Math.max(0, action.index), focusRegion: 'system'};
    case 'navigate-home': return navigateHomeFocus(state, action.direction, action.gameCount, action.systemCount);
    // m503: Down/X on a tile descends only when the experience's hub app has
    // fired its one-shot focusReady; otherwise the input is swallowed.
    case 'descend-hub':
      return action.hubReady && state.surface === 'home'
        && state.focusRegion === 'strand' && state.verticalPosition === 'home'
        ? {...state, verticalPosition: 'hub'}
        : state;
    case 'ascend-home':
      return state.verticalPosition === 'hub' ? {...state, verticalPosition: 'home'} : state;
  }
}

export function selectedShellGame(games: readonly GameInstall[], state: ShellState): GameInstall | undefined {
  return games[clamp(state.selectedIndex, games.length)];
}

/** Selection is remembered while focus visits Home's top band, but its card
 * focus passes belong exclusively to the strand focus region. */
export function isShellCardFocused(state: ShellState, index: number): boolean {
  return state.surface === 'home' && state.focusRegion === 'strand' && state.selectedIndex === index;
}

/** The compact icon is never used as a wide title plate. */
export function selectedShellBackground(game: GameInstall | undefined, surface: ShellSurface): string | undefined {
  return surface === 'home' ? game?.backgroundPath : undefined;
}
