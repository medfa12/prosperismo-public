import type {GameInstall, LauncherSettings} from './models';
import type {ProsperismoHostGateway} from './host';
import {createEmulatorArgs} from './emulatorArgs';
import {joinPath} from './paths';

function parentDirectory(path: string): string {
  const trimmed = path.replace(/[\\/]+$/g, '');
  const separator = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'));
  return separator < 0 ? '.' : trimmed.slice(0, separator);
}

export async function launchGame(
  host: ProsperismoHostGateway,
  game: GameInstall,
  launcherSettings: LauncherSettings,
): Promise<void> {
  const executable = await host.findEmulator();
  if (!executable) {
    throw new Error('Prosperismo emulator executable was not found.');
  }
  const patchPlan = game.titleId.trim()
    ? joinPath(parentDirectory(executable), '_Patches', `${game.titleId.trim().toUpperCase()}.json`)
    : undefined;
  const existingPatchPlan = patchPlan && (await host.fileExists(patchPlan)) ? patchPlan : undefined;
  const settings = game.customSettings ? game.settings : launcherSettings.global;
  await host.launch({
    executable,
    args: createEmulatorArgs(game, settings, existingPatchPlan),
    workingDirectory: parentDirectory(executable),
  });
}
