import type {EmulatorSettings, GameInstall} from './models';

const boolArg = (value: boolean): string => (value ? 'true' : 'false');

export function createEmulatorArgs(
  game: GameInstall,
  settings: EmulatorSettings,
  patchPlanPath?: string,
): string[] {
  const [width, height] = settings.screenResolution.split('x');
  if (!width || !height) {
    throw new Error(`Invalid screen resolution: ${settings.screenResolution}`);
  }

  const args = [
    '--screen-width', width,
    '--screen-height', height,
    '--vblank-frequency', String(settings.vblankFrequency),
    '--vulkan-validation', boolArg(settings.vulkanValidation),
    '--shader-validation', boolArg(settings.shaderValidation),
    '--shader-optimization-type', settings.shaderOptimization,
    '--shader-log-direction', settings.shaderLogDirection,
    '--shader-log-folder', settings.shaderLogFolder,
    '--command-buffer-dump', boolArg(settings.commandBufferDump),
    '--command-buffer-dump-folder', settings.commandBufferDumpFolder,
    '--printf-direction', settings.printfDirection,
    '--printf-output-file', settings.printfOutputFile,
    '--profiler-direction', settings.profilerDirection,
    '--spirv-debug-printf', 'false',
    '--ngg-rectlist-draw', boolArg(settings.nggRectlistDraw),
  ];
  if (settings.renderDoc) {
    args.push('--rd');
  }
  args.push('--game', game.executable ? game.ebootPath : game.baseDirectory);
  if (patchPlanPath) {
    args.push('--game-patch', patchPlanPath);
  }
  return args;
}
