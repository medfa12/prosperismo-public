import {applyPatchSelections, isPatchSupportedTitle, parsePatchPlan, patchPlanPath} from '../src/core/patches';

const source = JSON.stringify({patches: [{name: 'Unlock FPS'}, {name: 'Debug', enabled: false}], other: 4});

test('reads and writes patch enable flags while retaining the plan', () => {
  expect(parsePatchPlan(source, {'Unlock FPS': false})).toEqual([
    {name: 'Unlock FPS', enabled: false}, {name: 'Debug', enabled: false},
  ]);
  const changed = JSON.parse(applyPatchSelections(source, [{name: 'Unlock FPS', enabled: true}, {name: 'Debug', enabled: true}]));
  expect(changed.patches.map((item: {enabled: boolean}) => item.enabled)).toEqual([true, true]);
  expect(changed.other).toBe(4);
});

test('uses the Qt PPSA gate and emulator-adjacent patch directory', () => {
  expect(isPatchSupportedTitle(' ppsa12345 ')).toBe(true);
  expect(isPatchSupportedTitle('CUSA12345')).toBe(false);
  expect(patchPlanPath('C:\\Prosperismo\\prosperismo_emulator.exe', 'ppsa12345')).toBe('C:\\Prosperismo\\_Patches\\PPSA12345.json');
});
