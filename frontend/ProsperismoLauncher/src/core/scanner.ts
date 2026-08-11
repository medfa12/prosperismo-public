import type {EmulatorSettings, GameInstall} from './models';
import type {DirectoryEntry, ProsperismoHostGateway} from './host';
import {parseParamJson} from './metadata';
import {baseName, joinPath, windowsPathKey} from './paths';

async function safeList(host: ProsperismoHostGateway, path: string): Promise<DirectoryEntry[]> {
  try {
    return await host.listDirectory(path);
  } catch {
    return [];
  }
}

async function safeCanonical(host: ProsperismoHostGateway, path: string): Promise<string> {
  try {
    return await host.canonicalizePath(path);
  } catch {
    return path;
  }
}

export async function scanGameDirectories(
  host: ProsperismoHostGateway,
  roots: string[],
  globalSettings: EmulatorSettings,
  perGame: Record<string, EmulatorSettings>,
): Promise<GameInstall[]> {
  const games: GameInstall[] = [];
  const seen = new Set<string>();

  for (const root of roots) {
    // Qt starts with the root's children; an eboot directly in the configured root is not a game.
    const rootEntries = await safeList(host, root);
    const queue = rootEntries.filter(entry => entry.kind === 'directory' && !entry.symbolicLink);

    while (queue.length > 0) {
      const directory = queue.shift()!;
      const entries = await safeList(host, directory.path);
      const eboot = entries.find(
        entry => entry.kind === 'file' && entry.name.toLocaleLowerCase('en-US') === 'eboot.bin',
      );
      if (!eboot) {
        queue.push(
          ...entries.filter(entry => entry.kind === 'directory' && !entry.symbolicLink),
        );
        continue;
      }

      // Finding eboot.bin makes this directory a leaf in the scanner, matching the Qt launcher.
      const gamePath = await safeCanonical(host, directory.path);
      const key = windowsPathKey(gamePath);
      if (seen.has(key)) {
        continue;
      }
      seen.add(key);

      const paramEntry = entries.find(
        entry => entry.kind === 'directory' && entry.name.toLocaleLowerCase('en-US') === 'sce_sys',
      );
      let paramJson: string | undefined;
      let iconPath: string | undefined;
      let backgroundPath: string | undefined;
      let titleMusicPath: string | undefined;
      if (paramEntry) {
        const systemEntries = await safeList(host, paramEntry.path);
        const param = systemEntries.find(
          entry => entry.kind === 'file' && entry.name.toLocaleLowerCase('en-US') === 'param.json',
        );
        const icon = systemEntries.find(
          entry => entry.kind === 'file' && entry.name.toLocaleLowerCase('en-US') === 'icon0.png',
        );
        const background = systemEntries.find(
          entry => entry.kind === 'file' && entry.name.toLocaleLowerCase('en-US') === 'pic0.png',
        );
        const titleMusic = systemEntries.find(
          entry => entry.kind === 'file' && entry.name.toLocaleLowerCase('en-US') === 'snd0.at9',
        );
        if (param) {
          try {
            paramJson = await host.readTextFile(param.path);
          } catch {
            paramJson = undefined;
          }
        }
        iconPath = icon?.path;
        backgroundPath = background?.path;
        titleMusicPath = titleMusic?.path;
      }

      const metadata = parseParamJson(paramJson, baseName(directory.path));
      const custom = perGame[key];
      games.push({
        ...metadata,
        baseDirectory: directory.path,
        gamePath,
        ebootPath: eboot.path || joinPath(directory.path, 'eboot.bin'),
        iconPath,
        backgroundPath,
        titleMusicPath,
        artworkPath: iconPath,
        executable: 'eboot.bin',
        customSettings: Boolean(custom),
        settings: {...(custom ?? globalSettings)},
      });
    }
  }

  return games.sort((left, right) => left.titleName.localeCompare(right.titleName));
}
