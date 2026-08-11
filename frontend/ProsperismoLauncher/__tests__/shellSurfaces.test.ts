import type {GameInstall} from '../src/core/models';
import {
  SHELL_LIBRARY_METRICS,
  SHELL_OVERLAY_METRICS,
  SHELL_SETTINGS_METRICS,
  shellDialogButtonRowWidth,
  shellLibraryColumnLeft,
  shellLibraryContentHeight,
  shellLibraryMoveIndex,
  shellLibraryRowTop,
  shellLibraryScrollFor,
  shellModalShowEase,
  shellUtilityWidth,
  sortShellGames,
} from '../src/bigPicture/shellSurfaces';

function game(titleName: string, titleId: string): GameInstall {
  return {
    titleName,
    titleId,
    gameVersion: '1.00',
    firmwareVersion: '4.03',
    baseDirectory: 'C:\\games',
    gamePath: `C:\\games\\${titleId}`,
    ebootPath: `C:\\games\\${titleId}\\eboot.bin`,
    executable: 'eboot.bin',
    customSettings: false,
    settings: {
      screenResolution: '1280x720', vblankFrequency: 60, vulkanValidation: true,
      shaderValidation: true, shaderOptimization: 'Performance', shaderLogDirection: 'Silent',
      shaderLogFolder: '_Shaders', commandBufferDump: false, commandBufferDumpFolder: '_Buffers',
      printfDirection: 'Silent', printfOutputFile: '_prosperismo.txt', profilerDirection: 'None',
      renderDoc: false, nggRectlistDraw: true,
    },
  };
}

describe('recovered shell surface contracts', () => {
  it('packs the NPXS40071 five-column library to exactly 1576 pixels', () => {
    const packed = SHELL_LIBRARY_METRICS.tileWidth * SHELL_LIBRARY_METRICS.columns
      + SHELL_LIBRARY_METRICS.paddingHorizontal * (SHELL_LIBRARY_METRICS.columns - 1);
    expect(packed).toBe(SHELL_LIBRARY_METRICS.containerWidth);
    expect(shellLibraryColumnLeft(4)).toBe(1280);
    expect(shellLibraryRowTop(0)).toBe(82);
    expect(shellLibraryRowTop(5)).toBe(402);
  });

  it('clamps navigation and lands down on the final partial-row item', () => {
    expect(shellLibraryMoveIndex(0, 7, 'left')).toBe(0);
    expect(shellLibraryMoveIndex(0, 7, 'up')).toBe(0);
    expect(shellLibraryMoveIndex(0, 7, 'right')).toBe(1);
    expect(shellLibraryMoveIndex(1, 7, 'down')).toBe(6);
    expect(shellLibraryMoveIndex(6, 7, 'down')).toBe(6);
  });

  it('holds scroll between the first row and the content tail', () => {
    expect(shellLibraryScrollFor(0, 20, 976)).toBe(0);
    const scroll = shellLibraryScrollFor(19, 20, 976);
    expect(scroll).toBeGreaterThan(0);
    expect(scroll).toBeLessThanOrEqual(shellLibraryContentHeight(20) - 976);
  });

  it('sorts launcher games without depending on desktop-table state', () => {
    const games = [game('Zulu', 'CUSA00002'), game('alpha', 'CUSA00001')];
    expect(sortShellGames(games, 'titleName', 'ascending').map(item => item.titleName)).toEqual(['alpha', 'Zulu']);
    expect(sortShellGames(games, 'titleId', 'descending').map(item => item.titleId)).toEqual(['CUSA00002', 'CUSA00001']);
  });

  it('preserves settings, overlay, utility, and dialog identities', () => {
    expect(SHELL_SETTINGS_METRICS.listLeft + SHELL_SETTINGS_METRICS.listWidth + 304).toBe(1920);
    expect(SHELL_OVERLAY_METRICS.functionPanel.anchorY).toBe(126);
    expect(shellUtilityWidth(3)).toBe(312);
    expect(shellUtilityWidth(8)).toBe(416);
    expect(shellDialogButtonRowWidth(2)).toBe(784);
    expect(shellDialogButtonRowWidth(0)).toBe(0);
  });

  it('uses the recovered front-loaded modal curve', () => {
    expect(shellModalShowEase(0)).toBe(0);
    expect(shellModalShowEase(0.5)).toBeGreaterThan(0.94);
    expect(shellModalShowEase(1)).toBeCloseTo(0.9990234375, 10);
  });
});
