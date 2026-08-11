import {DEFAULT_EMULATOR_SETTINGS} from '../src/core/models';
import {sanitizeSettings, setPerGameSettings} from '../src/core/settings';

test('sanitizes settings while preserving original launcher defaults', () => {
  const result = sanitizeSettings({gameDirectories: [' D:\\Games ', 'D:\\Games'], global: {vblankFrequency: 120}});
  expect(result.gameDirectories).toEqual(['D:\\Games']);
  expect(result.global).toEqual({...DEFAULT_EMULATOR_SETTINGS, vblankFrequency: 120});
});

test('upgrades schema-one settings without fabricating compatibility data', () => {
  const result = sanitizeSettings({schemaVersion: 1, gameDirectories: ['D:\\Games']});
  expect(result.schemaVersion).toBe(2);
  expect(result.compatibility).toEqual({});
  expect(result.patchSelections).toEqual({});
  expect(result.library).toEqual({sortField: 'titleName', sortDirection: 'ascending'});
});

test('stores custom settings by normalized Windows path', () => {
  const base = sanitizeSettings({});
  const custom = {...DEFAULT_EMULATOR_SETTINGS, screenResolution: '1920x1080' as const};
  const changed = setPerGameSettings(base, 'D:\\Games\\ASTRO\\', custom);
  expect(changed.perGame['d:\\games\\astro']).toEqual(custom);
  expect(setPerGameSettings(changed, 'd:\\games\\astro', undefined).perGame).toEqual({});
});
