import {decodeRequiredFirmware, parseParamJson} from '../src/core/metadata';

describe('param.json metadata', () => {
  test('uses default locale, app version, and decoded firmware', () => {
    const metadata = parseParamJson(JSON.stringify({
      titleId: 'PPSA00001',
      appVersion: '01.020.000',
      contentVersion: '09.999.999',
      requiredSystemSoftwareVersion: '0x0301000000000000',
      localizedParameters: {
        defaultLanguage: 'ja-JP',
        'en-US': {titleName: 'English name'},
        'ja-JP': {titleName: 'Default name'},
      },
    }), 'Fallback');

    expect(metadata).toEqual({
      titleName: 'Default name',
      titleId: 'PPSA00001',
      gameVersion: '01.020.000',
      firmwareVersion: '3.01',
    });
  });

  test('falls back through en-US, any locale, folder, and contentVersion', () => {
    expect(parseParamJson(JSON.stringify({
      contentVersion: '01.100.000',
      localizedParameters: {'fr-FR': {titleName: 'Jeu'}},
    }), 'Folder').titleName).toBe('Jeu');
    expect(parseParamJson('{bad json', 'Folder').titleName).toBe('Folder');
    expect(parseParamJson('{"contentVersion":"02.00"}', 'Folder').gameVersion).toBe('02.00');
  });

  test('matches the Qt firmware encoding contract', () => {
    expect(decodeRequiredFirmware('0x0456010000000000')).toBe('4.56.01');
    expect(decodeRequiredFirmware('0x0456000000000000')).toBe('4.56');
    expect(decodeRequiredFirmware('0xZZ56000000000000')).toBe('');
  });
});
