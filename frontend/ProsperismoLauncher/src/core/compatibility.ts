import type {CompatibilityEntry, GameStatus} from './models';

const STATUS_ALIASES: Record<string, GameStatus> = {
  unknown: 'Unknown',
  ingame: 'InGame',
  'in game': 'InGame',
  mainmenu: 'MainMenu',
  'main menu': 'MainMenu',
  logo: 'Logo',
  doesntboot: 'DoesntBoot',
  "doesn't boot": 'DoesntBoot',
};

export const GAME_STATUSES: readonly GameStatus[] = [
  'Unknown',
  'MainMenu',
  'InGame',
  'Logo',
  'DoesntBoot',
];

// Retained solely as the Qt launcher's current compatibility-data endpoint.
// It is not product branding and is never rendered in the UI.
export const COMPATIBILITY_DATABASE_URL =
  'https://github.com/Nmzik/KytyPS5/releases/download/compat-db/compatibility_db.json';

export interface CompatibilityFetchResponse {
  ok: boolean;
  status: number;
  text(): Promise<string>;
}

export type CompatibilityFetcher = (
  url: string,
  options?: {headers?: Record<string, string>},
) => Promise<CompatibilityFetchResponse>;

export function gameStatusLabel(status: GameStatus): string {
  switch (status) {
    case 'MainMenu': return 'Main menu';
    case 'InGame': return 'In game';
    case 'DoesntBoot': return "Doesn't boot";
    default: return status;
  }
}

export function parseCompatibilityDatabase(text: string): Record<string, CompatibilityEntry> {
  const parsed: unknown = JSON.parse(text);
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('Compatibility database root must be an object.');
  }
  const result: Record<string, CompatibilityEntry> = {};
  Object.entries(parsed).forEach(([rawTitleId, rawEntry]) => {
    const titleId = rawTitleId.trim().toUpperCase();
    if (!titleId || !rawEntry || typeof rawEntry !== 'object' || Array.isArray(rawEntry)) {
      return;
    }
    const entry = rawEntry as Record<string, unknown>;
    const rawStatus = typeof entry.status === 'string' ? entry.status.trim().toLowerCase() : '';
    result[titleId] = {
      status: STATUS_ALIASES[rawStatus] ?? 'Unknown',
      comment: typeof entry.comment === 'string' ? entry.comment : '',
    };
  });
  return result;
}

export function serializeCompatibilityDatabase(
  entries: Record<string, CompatibilityEntry>,
): string {
  const ordered: Record<string, CompatibilityEntry> = {};
  Object.keys(entries).sort().forEach(key => {
    ordered[key.trim().toUpperCase()] = entries[key];
  });
  return JSON.stringify(ordered, null, 2);
}

/** Remote data fills gaps; locally edited status/comments always win. */
export function mergeCompatibilityEntries(
  remote: Record<string, CompatibilityEntry>,
  local: Record<string, CompatibilityEntry>,
): Record<string, CompatibilityEntry> {
  return {...remote, ...local};
}

export async function refreshCompatibilityDatabase(
  fetcher: CompatibilityFetcher,
  wait: (milliseconds: number) => Promise<void> = milliseconds =>
    new Promise(resolve => setTimeout(resolve, milliseconds)),
): Promise<Record<string, CompatibilityEntry>> {
  let lastError: Error | undefined;
  // Qt makes one initial request followed by three retries with 750ms linear backoff.
  for (let attempt = 0; attempt < 4; attempt += 1) {
    if (attempt > 0) {
      await wait(750 * attempt);
    }
    try {
      const response = await fetcher(COMPATIBILITY_DATABASE_URL, {
        headers: {'User-Agent': 'Prosperismo-Launcher'},
      });
      if (!response.ok) {
        throw new Error(`Compatibility download failed with HTTP ${response.status}.`);
      }
      return parseCompatibilityDatabase(await response.text());
    } catch (reason) {
      lastError = reason instanceof Error ? reason : new Error(String(reason));
    }
  }
  throw lastError ?? new Error('Compatibility download failed.');
}
