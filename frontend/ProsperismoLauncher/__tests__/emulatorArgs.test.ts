import {createEmulatorArgs} from '../src/core/emulatorArgs';
import {DEFAULT_EMULATOR_SETTINGS, type GameInstall} from '../src/core/models';

const game: GameInstall = {
  titleName: 'Test',
  titleId: 'PPSA00001',
  gameVersion: '1.00',
  firmwareVersion: '3.00',
  baseDirectory: 'D:\\Games\\Test',
  gamePath: 'D:\\Games\\Test',
  ebootPath: 'D:\\Games\\Test\\eboot.bin',
  executable: 'eboot.bin',
  customSettings: false,
  settings: DEFAULT_EMULATOR_SETTINGS,
};

test('emits the original launcher arguments in exact order', () => {
  expect(createEmulatorArgs(game, {
    ...DEFAULT_EMULATOR_SETTINGS,
    screenResolution: '1920x1080',
    vblankFrequency: 120,
    vulkanValidation: false,
    shaderLogDirection: 'File',
    commandBufferDump: true,
    printfDirection: 'Console',
    profilerDirection: 'Network',
    renderDoc: true,
    nggRectlistDraw: false,
  }, 'C:\\Prosperismo\\_Patches\\PPSA00001.json')).toEqual([
    '--screen-width', '1920',
    '--screen-height', '1080',
    '--vblank-frequency', '120',
    '--vulkan-validation', 'false',
    '--shader-validation', 'true',
    '--shader-optimization-type', 'Performance',
    '--shader-log-direction', 'File',
    '--shader-log-folder', '_Shaders',
    '--command-buffer-dump', 'true',
    '--command-buffer-dump-folder', '_Buffers',
    '--printf-direction', 'Console',
    '--printf-output-file', '_prosperismo.txt',
    '--profiler-direction', 'Network',
    '--spirv-debug-printf', 'false',
    '--ngg-rectlist-draw', 'false',
    '--rd',
    '--game', 'D:\\Games\\Test\\eboot.bin',
    '--game-patch', 'C:\\Prosperismo\\_Patches\\PPSA00001.json',
  ]);
});
