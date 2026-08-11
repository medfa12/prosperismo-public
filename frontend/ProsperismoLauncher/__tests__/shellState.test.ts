import {INITIAL_SHELL_STATE, isShellCardFocused, navigateHomeFocus, reduceShellState, selectedShellBackground} from '../src/bigPicture/shellState';
import {SHELL_FOCUSED_TILE_SCALE, SHELL_METRICS, shellEaseOutBlast, shellHomeFocusTarget, shellTileBaseX} from '../src/bigPicture/shellMetrics';
import {homeTileLeft} from '../src/bigPicture/shellHomeMotion';

describe('Sony-grounded shell state', () => {
  it('clamps strand selection to installed games', () => {
    const moved = reduceShellState(INITIAL_SHELL_STATE, {type: 'select-game', index: 12, gameCount: 4});
    expect(moved.selectedIndex).toBe(3);
  });

  it('keeps settings and home focus regions separate', () => {
    const settings = reduceShellState(INITIAL_SHELL_STATE, {type: 'open-settings'});
    expect(settings.surface).toBe('settings');
    expect(settings.focusRegion).toBe('content');
    const home = reduceShellState(settings, {type: 'home'});
    expect(home.surface).toBe('home');
    expect(home.focusRegion).toBe('strand');
  });

  it('matches the firmware strand packing constants', () => {
    expect(SHELL_FOCUSED_TILE_SCALE).toBeCloseTo(168 / 106, 8);
    expect(shellTileBaseX(2, 2)).toBeCloseTo(203, 8);
    expect(shellTileBaseX(1, 2)).toBeCloseTo(50, 8);
    expect(shellTileBaseX(3, 2)).toBeCloseTo(356, 8);
    expect(SHELL_METRICS.strand.top + SHELL_METRICS.strand.titleTop).toBe(232);
  });

  it('agrees with the HOME m531 solver for every unfocused tile', () => {
    for (let index = 0; index < SHELL_METRICS.strand.maxItems; index += 1) {
      if (index !== 5) {
        expect(shellTileBaseX(index, 5)).toBe(homeTileLeft(index, 5));
      }
    }
  });

  it('keeps the selected game while system focus moves independently', () => {
    const selected = reduceShellState(INITIAL_SHELL_STATE, {type: 'select-game', index: 3, gameCount: 5});
    const system = reduceShellState(selected, {type: 'select-system', index: 1});
    expect(system.selectedIndex).toBe(3);
    expect(system.focusRegion).toBe('system');
    expect(isShellCardFocused(system, 3)).toBe(false);
    expect(isShellCardFocused(selected, 3)).toBe(true);
  });

  it('keeps the native card line separate from the control-centre focus width', () => {
    expect(SHELL_METRICS.focusLineWidth).toBe(3);
    expect(SHELL_METRICS.focusLineOffset).toBe(3);
  });

  it('uses the recovered Normal HOME transition duration', () => {
    expect(SHELL_METRICS.titleBackgroundTransitionMs).toBeCloseTo(633.33334, 4);
  });

  it('uses pic0 only for the selected Home title plate', () => {
    const game = {backgroundPath: 'D:\\Games\\Astro\\sce_sys\\pic0.png'} as any;
    expect(selectedShellBackground(game, 'home')).toBe(game.backgroundPath);
    expect(selectedShellBackground(game, 'settings')).toBeUndefined();
  });

  it('uses the recovered named-neighbour graph without wrapping', () => {
    const spaces = navigateHomeFocus(INITIAL_SHELL_STATE, 'up', 5, 3);
    expect(spaces.focusRegion).toBe('spaces');
    expect(navigateHomeFocus(spaces, 'left', 5, 3)).toBe(spaces);

    const media = navigateHomeFocus(spaces, 'right', 5, 3);
    expect(media.space).toBe('games');
    expect(media.spaceCursor).toBe('media');
    expect(media.focusRegion).toBe('spaces');

    const system = navigateHomeFocus(media, 'right', 5, 3);
    expect(system.focusRegion).toBe('system');
    expect(system.systemIndex).toBe(0);
    expect(navigateHomeFocus(system, 'left', 5, 3).focusRegion).toBe('spaces');
    expect(navigateHomeFocus(system, 'down', 5, 3).focusRegion).toBe('strand');
  });

  it('clamps within the strand and system regions', () => {
    expect(navigateHomeFocus(INITIAL_SHELL_STATE, 'left', 5, 3).selectedIndex).toBe(0);
    const lastSystem = {...INITIAL_SHELL_STATE, focusRegion: 'system' as const, systemIndex: 2};
    expect(navigateHomeFocus(lastSystem, 'right', 5, 3).systemIndex).toBe(2);
  });

  it('gives the library shortcut its own terminal focus node', () => {
    const lastGame = {...INITIAL_SHELL_STATE, selectedIndex: 4};
    const library = navigateHomeFocus(lastGame, 'right', 5, 3);
    expect(library.focusRegion).toBe('library-shortcut');
    const restored = navigateHomeFocus(library, 'left', 5, 3);
    expect(restored.focusRegion).toBe('strand');
    expect(restored.selectedIndex).toBe(4);
  });

  it('positions the single HOME focus owner on recovered card and system geometry', () => {
    expect(shellHomeFocusTarget('strand')).toMatchObject({kind: 'card', x: 166, y: 120, width: 180, height: 180});
    expect(shellHomeFocusTarget('system', 0)).toMatchObject({kind: 'system', x: 1364, y: 35, width: 56, height: 56});
    expect(shellHomeFocusTarget('system', 2).x).toBe(1572);
  });

  it('uses the exact EaseOutBlast r=10 i=0.5 curve', () => {
    expect(shellEaseOutBlast(0)).toBe(0);
    expect(shellEaseOutBlast(0.5)).toBeCloseTo(1 - Math.pow(0.75, 10), 12);
    expect(shellEaseOutBlast(1)).toBeCloseTo(1023 / 1024, 12);
  });
});
