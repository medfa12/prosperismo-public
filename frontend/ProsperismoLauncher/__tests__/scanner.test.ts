import type {DirectoryEntry, ProsperismoHostGateway} from '../src/core/host';
import {DEFAULT_EMULATOR_SETTINGS} from '../src/core/models';
import {scanGameDirectories} from '../src/core/scanner';

const directory = (name: string, path: string, symbolicLink = false): DirectoryEntry => ({
  name, path, kind: 'directory', symbolicLink,
});
const file = (name: string, path: string): DirectoryEntry => ({name, path, kind: 'file'});

function fakeHost(tree: Record<string, DirectoryEntry[]>): ProsperismoHostGateway & {listDirectory: jest.Mock} {
  return {
    listDirectory: jest.fn(async (path: string) => tree[path] ?? []),
    readTextFile: jest.fn(async (path: string) => path.endsWith('param.json')
      ? JSON.stringify({
        titleId: 'PPSA00001',
        contentVersion: '01.00',
        localizedParameters: {'en-US': {titleName: 'Astro Test'}},
      })
      : ''),
    canonicalizePath: jest.fn(async (path: string) => path.replace('Alias', 'GameA')),
    chooseGameDirectories: jest.fn(),
    loadLauncherSettings: jest.fn(),
    saveLauncherSettings: jest.fn(),
    findEmulator: jest.fn(),
    fileExists: jest.fn(),
    launch: jest.fn(),
  };
}

test('performs breadth-first child scanning, skips links, stops at eboot, and deduplicates', async () => {
  const tree: Record<string, DirectoryEntry[]> = {
    'D:\\Games': [
      file('eboot.bin', 'D:\\Games\\eboot.bin'),
      directory('GameA', 'D:\\Games\\GameA'),
      directory('Container', 'D:\\Games\\Container'),
      directory('Linked', 'D:\\Games\\Linked', true),
    ],
    'D:\\Games\\GameA': [
      file('eboot.bin', 'D:\\Games\\GameA\\eboot.bin'),
      directory('sce_sys', 'D:\\Games\\GameA\\sce_sys'),
      directory('NestedMustNotScan', 'D:\\Games\\GameA\\NestedMustNotScan'),
    ],
    'D:\\Games\\GameA\\sce_sys': [
      file('param.json', 'D:\\Games\\GameA\\sce_sys\\param.json'),
      file('icon0.png', 'D:\\Games\\GameA\\sce_sys\\icon0.png'),
      file('pic0.png', 'D:\\Games\\GameA\\sce_sys\\pic0.png'),
      file('snd0.at9', 'D:\\Games\\GameA\\sce_sys\\snd0.at9'),
    ],
    'D:\\Games\\Container': [directory('Alias', 'D:\\Games\\Alias')],
    'D:\\Games\\Alias': [file('EBOOT.BIN', 'D:\\Games\\Alias\\EBOOT.BIN')],
  };
  const host = fakeHost(tree);
  const games = await scanGameDirectories(host, ['D:\\Games'], DEFAULT_EMULATOR_SETTINGS, {});

  expect(games).toHaveLength(1);
  expect(games[0]).toMatchObject({
    titleName: 'Astro Test',
    titleId: 'PPSA00001',
    iconPath: 'D:\\Games\\GameA\\sce_sys\\icon0.png',
    backgroundPath: 'D:\\Games\\GameA\\sce_sys\\pic0.png',
    titleMusicPath: 'D:\\Games\\GameA\\sce_sys\\snd0.at9',
    artworkPath: 'D:\\Games\\GameA\\sce_sys\\icon0.png',
  });
  expect(host.listDirectory).not.toHaveBeenCalledWith('D:\\Games\\Linked');
  expect(host.listDirectory).not.toHaveBeenCalledWith('D:\\Games\\GameA\\NestedMustNotScan');
});

test('applies per-game settings using the canonical case-insensitive path key', async () => {
  const host = fakeHost({
    'D:\\Games': [directory('GameA', 'D:\\Games\\GameA')],
    'D:\\Games\\GameA': [file('eboot.bin', 'D:\\Games\\GameA\\eboot.bin')],
  });
  const custom = {...DEFAULT_EMULATOR_SETTINGS, vblankFrequency: 30};
  const games = await scanGameDirectories(
    host,
    ['D:\\Games'],
    DEFAULT_EMULATOR_SETTINGS,
    {'d:\\games\\gamea': custom},
  );
  expect(games[0].customSettings).toBe(true);
  expect(games[0].settings.vblankFrequency).toBe(30);
});
