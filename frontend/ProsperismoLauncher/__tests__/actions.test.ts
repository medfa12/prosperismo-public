import {confirmedSaveDataRemoval, saveDataCandidatePaths} from '../src/core/actions';

test('derives only emulator directory and parent save-data candidates like Qt', () => {
  expect(saveDataCandidatePaths('C:\\Prosperismo\\bin\\prosperismo_emulator.exe', 'PPSA00001')).toEqual([
    'C:\\Prosperismo\\bin\\_SaveData\\PPSA00001',
    'C:\\Prosperismo\\_SaveData\\PPSA00001',
  ]);
});

test('requires exact displayed paths before constructing destructive request', () => {
  expect(() => confirmedSaveDataRemoval('', [])).toThrow();
  expect(confirmedSaveDataRemoval('PPSA1', ['C:\\save'])).toEqual({titleId: 'PPSA1', paths: ['C:\\save'], confirmed: true});
});
