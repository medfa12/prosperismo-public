import type {LauncherSettings, PatchSelection} from './models';
import type {ProsperismoHostGateway} from './host';
import {joinPath} from './paths';

type JsonObject = Record<string, unknown>;

export function isPatchSupportedTitle(titleId: string): boolean {
  return titleId.trim().toUpperCase().startsWith('PPSA');
}

export function patchPlanPath(emulatorPath: string, titleId: string): string {
  const parent = emulatorPath.replace(/[\\/][^\\/]+$/, '');
  return joinPath(parent || '.', '_Patches', `${titleId.trim().toUpperCase()}.json`);
}

export function parsePatchPlan(
  text: string,
  overrides: Record<string, boolean> = {},
): PatchSelection[] {
  const root: unknown = JSON.parse(text);
  if (!root || typeof root !== 'object' || Array.isArray(root)) {
    throw new Error('Patch plan root must be an object.');
  }
  const patches = (root as JsonObject).patches;
  if (!Array.isArray(patches)) {
    return [];
  }
  return patches.flatMap(value => {
    if (!value || typeof value !== 'object' || Array.isArray(value)) {
      return [];
    }
    const patch = value as JsonObject;
    if (typeof patch.name !== 'string' || !patch.name) {
      return [];
    }
    return [{
      name: patch.name,
      enabled: overrides[patch.name] ?? (typeof patch.enabled === 'boolean' ? patch.enabled : true),
    }];
  });
}

export function applyPatchSelections(text: string, selections: PatchSelection[]): string {
  const root = JSON.parse(text) as JsonObject;
  const enabled = new Map(selections.map(item => [item.name, item.enabled]));
  if (Array.isArray(root.patches)) {
    root.patches = root.patches.map(value => {
      if (!value || typeof value !== 'object' || Array.isArray(value)) {
        return value;
      }
      const patch = value as JsonObject;
      return typeof patch.name === 'string' && enabled.has(patch.name)
        ? {...patch, enabled: enabled.get(patch.name)}
        : patch;
    });
  }
  return JSON.stringify(root, null, 2);
}

export async function loadPatchPlan(
  host: ProsperismoHostGateway,
  emulatorPath: string,
  titleId: string,
  settings: LauncherSettings,
): Promise<{path: string; source: string; patches: PatchSelection[]} | undefined> {
  if (!isPatchSupportedTitle(titleId)) {
    return undefined;
  }
  const path = patchPlanPath(emulatorPath, titleId);
  if (!(await host.fileExists(path))) {
    return undefined;
  }
  const source = await host.readTextFile(path);
  return {path, source, patches: parsePatchPlan(source, settings.patchSelections[titleId.trim().toUpperCase()])};
}
