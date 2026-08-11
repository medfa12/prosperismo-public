import type {DestructiveDirectoryRequest, ProsperismoHostGateway} from './host';
import {joinPath, windowsPathKey} from './paths';

function parentDirectory(path: string): string {
  const trimmed = path.replace(/[\\/]+$/g, '');
  const separator = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'));
  return separator < 0 ? '.' : trimmed.slice(0, separator);
}

export function saveDataCandidatePaths(emulatorPath: string, titleId: string): string[] {
  const cleanId = titleId.trim();
  if (!cleanId) {
    return [];
  }
  const appDirectory = parentDirectory(emulatorPath);
  const roots = [appDirectory, parentDirectory(appDirectory)];
  const seen = new Set<string>();
  return roots.flatMap(root => {
    const path = joinPath(root, '_SaveData', cleanId);
    const key = windowsPathKey(path);
    if (seen.has(key)) {
      return [];
    }
    seen.add(key);
    return [path];
  });
}

export async function existingSaveDataPaths(
  host: ProsperismoHostGateway,
  emulatorPath: string,
  titleId: string,
): Promise<string[]> {
  const candidates = saveDataCandidatePaths(emulatorPath, titleId);
  const exists = await Promise.all(candidates.map(path => host.fileExists(path)));
  return candidates.filter((_, index) => exists[index]);
}

export function confirmedSaveDataRemoval(
  titleId: string,
  displayedPaths: string[],
): DestructiveDirectoryRequest {
  if (!titleId.trim() || displayedPaths.length === 0) {
    throw new Error('Save-data removal requires a title id and at least one displayed path.');
  }
  return {titleId: titleId.trim(), paths: [...displayedPaths], confirmed: true};
}

export function hostActionAvailability(host: ProsperismoHostGateway) {
  return {
    openGameFolder: Boolean(host.openPath),
    removeSaveData: Boolean(host.removeDirectories),
    writePatchPlan: Boolean(host.writeTextFile),
    readTrophies: Boolean(host.readBinaryFile),
    processEvents: Boolean(host.subscribeProcessEvents),
  };
}
