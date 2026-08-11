import {applyProcessEvent, beginSession, launchedSession} from '../src/core/process';
import {DEFAULT_EMULATOR_SETTINGS, type GameInstall} from '../src/core/models';

const game: GameInstall = {titleName: 'Astro', titleId: 'PPSA', gameVersion: '', firmwareVersion: '', baseDirectory: 'D:\\Astro', gamePath: 'D:\\Astro', ebootPath: 'D:\\Astro\\eboot.bin', executable: 'eboot.bin', customSettings: false, settings: DEFAULT_EMULATOR_SETTINGS};

test('keeps launch-only provenance distinct from a host-tracked exit', () => {
  const launching = beginSession(game, false);
  expect(launchedSession(launching)).toMatchObject({phase: 'running', tracking: 'launch-only'});
  expect(applyProcessEvent(beginSession(game, true), {phase: 'exited', exitCode: 0})).toMatchObject({phase: 'exited', tracking: 'host-events', exitCode: 0});
});
