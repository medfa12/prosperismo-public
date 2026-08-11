import type {CompatibilityEntry, GameStatus, LauncherSettings} from './models';
import {DEFAULT_EMULATOR_SETTINGS, DEFAULT_LAUNCHER_SETTINGS} from './models';
import type {ProsperismoHostGateway} from './host';
import {windowsPathKey} from './paths';

export function sanitizeSettings(input: unknown): LauncherSettings {
  if (!input || typeof input !== 'object') {
    return {
      ...DEFAULT_LAUNCHER_SETTINGS,
      global: {...DEFAULT_EMULATOR_SETTINGS},
      gameDirectories: [],
      perGame: {},
      compatibility: {},
      patchSelections: {},
      library: {...DEFAULT_LAUNCHER_SETTINGS.library},
    };
  }
  const value = input as Partial<LauncherSettings>;
  const gameDirectories = Array.isArray(value.gameDirectories)
    ? [...new Set(value.gameDirectories.filter(item => typeof item === 'string' && item.trim()).map(item => item.trim()))]
    : [];
  const compatibility = sanitizeCompatibility(value.compatibility);
  const libraryInput = value.library;
  const sortFields = ['titleName', 'titleId', 'gameVersion', 'firmwareVersion', 'gamePath', 'status', 'comment'];
  return {
    schemaVersion: 2,
    gameDirectories,
    global: {...DEFAULT_EMULATOR_SETTINGS, ...(value.global ?? {})},
    perGame: value.perGame && typeof value.perGame === 'object' ? value.perGame : {},
    compatibility,
    patchSelections: value.patchSelections && typeof value.patchSelections === 'object'
      ? value.patchSelections
      : {},
    library: {
      sortField: libraryInput && sortFields.includes(libraryInput.sortField)
        ? libraryInput.sortField
        : 'titleName',
      sortDirection: libraryInput?.sortDirection === 'descending' ? 'descending' : 'ascending',
    },
  };
}

const STATUSES: GameStatus[] = ['Unknown', 'MainMenu', 'InGame', 'Logo', 'DoesntBoot'];

function sanitizeCompatibility(input: unknown): Record<string, CompatibilityEntry> {
  if (!input || typeof input !== 'object' || Array.isArray(input)) {
    return {};
  }
  const result: Record<string, CompatibilityEntry> = {};
  Object.entries(input).forEach(([key, raw]) => {
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
      return;
    }
    const entry = raw as Partial<CompatibilityEntry>;
    result[key.trim().toUpperCase()] = {
      status: STATUSES.includes(entry.status as GameStatus) ? entry.status as GameStatus : 'Unknown',
      comment: typeof entry.comment === 'string' ? entry.comment : '',
    };
  });
  return result;
}

export async function loadSettings(host: ProsperismoHostGateway): Promise<LauncherSettings> {
  const json = await host.loadLauncherSettings();
  if (!json) {
    return sanitizeSettings(undefined);
  }
  try {
    return sanitizeSettings(JSON.parse(json));
  } catch {
    return sanitizeSettings(undefined);
  }
}

export function setCompatibility(
  settings: LauncherSettings,
  titleId: string,
  value: CompatibilityEntry,
): LauncherSettings {
  const key = titleId.trim().toUpperCase();
  if (!key) {
    return settings;
  }
  return {
    ...settings,
    compatibility: {...settings.compatibility, [key]: {...value}},
  };
}

export function setPatchSelection(
  settings: LauncherSettings,
  titleId: string,
  patchName: string,
  enabled: boolean,
): LauncherSettings {
  const key = titleId.trim().toUpperCase();
  if (!key || !patchName) {
    return settings;
  }
  return {
    ...settings,
    patchSelections: {
      ...settings.patchSelections,
      [key]: {...settings.patchSelections[key], [patchName]: enabled},
    },
  };
}

export async function saveSettings(
  host: ProsperismoHostGateway,
  settings: LauncherSettings,
): Promise<void> {
  await host.saveLauncherSettings(JSON.stringify(settings, null, 2));
}

export function setPerGameSettings(
  settings: LauncherSettings,
  gamePath: string,
  value: LauncherSettings['global'] | undefined,
): LauncherSettings {
  const perGame = {...settings.perGame};
  const key = windowsPathKey(gamePath);
  if (value) {
    perGame[key] = {...value};
  } else {
    delete perGame[key];
  }
  return {...settings, perGame};
}
