import type {
  CompatibilityEntry,
  GameInstall,
  LibrarySortField,
  SortDirection,
} from './models';

export interface LibraryGame extends GameInstall {
  compatibility: CompatibilityEntry;
}

function versionParts(value: string): number[] {
  return value.split('.').map(part => Number.parseInt(part, 10)).map(part => Number.isFinite(part) ? part : 0);
}

function compareVersions(left: string, right: string): number {
  if (!left || !right) {
    return left ? 1 : right ? -1 : 0;
  }
  const a = versionParts(left);
  const b = versionParts(right);
  const length = Math.max(a.length, b.length);
  for (let index = 0; index < length; index += 1) {
    const difference = (a[index] ?? 0) - (b[index] ?? 0);
    if (difference) {
      return difference;
    }
  }
  return 0;
}

export function filterAndSortGames(
  games: GameInstall[],
  compatibility: Record<string, CompatibilityEntry>,
  query: string,
  sortField: LibrarySortField,
  direction: SortDirection,
): LibraryGame[] {
  const needle = query.trim().toLocaleLowerCase('en-US');
  const enriched = games.map(game => ({
    ...game,
    compatibility: compatibility[game.titleId.trim().toUpperCase()] ?? {status: 'Unknown', comment: ''},
  }));
  const filtered = enriched.filter(game => !needle ||
    game.titleName.toLocaleLowerCase('en-US').includes(needle) ||
    game.titleId.toLocaleLowerCase('en-US').includes(needle));
  const sign = direction === 'descending' ? -1 : 1;
  return filtered.sort((left, right) => {
    let compared = 0;
    switch (sortField) {
      case 'gameVersion': compared = compareVersions(left.gameVersion, right.gameVersion); break;
      case 'firmwareVersion': compared = compareVersions(left.firmwareVersion, right.firmwareVersion); break;
      case 'status': compared = left.compatibility.status.localeCompare(right.compatibility.status); break;
      case 'comment': compared = left.compatibility.comment.localeCompare(right.compatibility.comment); break;
      default: compared = left[sortField].localeCompare(right[sortField]); break;
    }
    return (compared || left.gamePath.localeCompare(right.gamePath)) * sign;
  });
}

export function fileImageUri(path: string | undefined): string | undefined {
  if (!path) {
    return undefined;
  }
  const normalized = path.replace(/\\/g, '/');
  return encodeURI(normalized.startsWith('/') ? `file://${normalized}` : `file:///${normalized}`);
}
