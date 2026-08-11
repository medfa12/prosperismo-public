import React from 'react';
import {createRoot} from 'react-dom/client';
import {BigPictureShell} from '../src/bigPicture/BigPictureShell';
import {DEFAULT_EMULATOR_SETTINGS, type GameInstall, type LauncherSettings} from '../src/core/models';

/**
 * Harness for the browser layout preview. See web/vite.config.mts for why this
 * exists and what it does NOT represent.
 *
 * The titles below are obviously fictional placeholders so a screenshot can
 * never be mistaken for a real library or for a fidelity capture.
 */

const TITLES = [
  'Placeholder One', 'Placeholder Two', 'Placeholder Three', 'Placeholder Four',
  'Placeholder Five', 'Placeholder Six', 'Placeholder Seven', 'Placeholder Eight',
];

const games: GameInstall[] = TITLES.map((titleName, index) => ({
  titleName,
  titleId: `PPSA0000${index}`,
  gameVersion: '01.00',
  firmwareVersion: '12.40',
  baseDirectory: `/preview/${index}`,
  gamePath: `/preview/${index}`,
  ebootPath: `/preview/${index}/eboot.bin`,
  executable: 'eboot.bin',
  customSettings: false,
  settings: DEFAULT_EMULATOR_SETTINGS,
}));

const settings: LauncherSettings = {
  schemaVersion: 2,
  gameDirectories: [],
  global: DEFAULT_EMULATOR_SETTINGS,
  perGame: {},
  compatibility: {},
  patchSelections: {},
  library: {sortField: 'titleName', sortDirection: 'ascending'},
};

function Preview() {
  return <BigPictureShell
    games={games}
    onDesktop={() => console.log('[preview] desktop requested')}
    onDismissError={() => undefined}
    onLaunch={game => console.log('[preview] launch', game.titleName)}
    onSaveSettings={() => undefined}
    settings={settings}
  />;
}

const container = document.getElementById('root');
if (container) {
  createRoot(container).render(<React.StrictMode><Preview /></React.StrictMode>);
}
