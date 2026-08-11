import {fileImageUri, filterAndSortGames} from '../src/core/library';
import {DEFAULT_EMULATOR_SETTINGS, type GameInstall} from '../src/core/models';

const game = (titleName: string, titleId: string, version: string): GameInstall => ({
  titleName, titleId, gameVersion: version, firmwareVersion: '', baseDirectory: `D:\\${titleName}`,
  gamePath: `D:\\${titleName}`, ebootPath: `D:\\${titleName}\\eboot.bin`, executable: 'eboot.bin',
  customSettings: false, settings: {...DEFAULT_EMULATOR_SETTINGS},
});

test('filters by name or serial and sorts versions numerically', () => {
  const games = [game('Ten', 'PPSA2', '1.10'), game('Two', 'PPSA1', '1.2')];
  expect(filterAndSortGames(games, {}, '', 'gameVersion', 'ascending').map(item => item.titleName)).toEqual(['Two', 'Ten']);
  expect(filterAndSortGames(games, {}, 'ppsa2', 'titleName', 'ascending').map(item => item.titleName)).toEqual(['Ten']);
});

test('creates a Windows file image URI without inventing a web URL', () => {
  expect(fileImageUri('D:\\Games\\Astro\\sce_sys\\pic0.png')).toBe('file:///D:/Games/Astro/sce_sys/pic0.png');
});
