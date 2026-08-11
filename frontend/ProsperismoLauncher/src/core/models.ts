export type Resolution = '1280x720' | '1920x1080';
export type ShaderOptimization = 'None' | 'Size' | 'Performance';
export type LogDirection = 'Silent' | 'Console' | 'File';
export type ProfilerDirection = 'None' | 'Network';
export type GameStatus = 'Unknown' | 'MainMenu' | 'InGame' | 'Logo' | 'DoesntBoot';
export type LibrarySortField =
  | 'titleName'
  | 'titleId'
  | 'gameVersion'
  | 'firmwareVersion'
  | 'gamePath'
  | 'status'
  | 'comment';
export type SortDirection = 'ascending' | 'descending';

export interface EmulatorSettings {
  screenResolution: Resolution;
  vblankFrequency: number;
  vulkanValidation: boolean;
  shaderValidation: boolean;
  shaderOptimization: ShaderOptimization;
  shaderLogDirection: LogDirection;
  shaderLogFolder: string;
  commandBufferDump: boolean;
  commandBufferDumpFolder: string;
  printfDirection: LogDirection;
  printfOutputFile: string;
  profilerDirection: ProfilerDirection;
  renderDoc: boolean;
  nggRectlistDraw: boolean;
}

export interface GameMetadata {
  titleName: string;
  titleId: string;
  gameVersion: string;
  firmwareVersion: string;
}

export interface GameInstall extends GameMetadata {
  baseDirectory: string;
  gamePath: string;
  ebootPath: string;
  /** sce_sys/icon0.png, used for compact library tiles. */
  iconPath?: string;
  /** sce_sys/pic0.png, used as the selected game's wide background. */
  backgroundPath?: string;
  /** sce_sys/snd0.at9, used as the selected game's title preview music. */
  titleMusicPath?: string;
  /** Legacy frontend alias retained for schema/source compatibility. */
  artworkPath?: string;
  executable: string;
  customSettings: boolean;
  settings: EmulatorSettings;
}

export interface CompatibilityEntry {
  status: GameStatus;
  comment: string;
}

export interface PatchSelection {
  name: string;
  enabled: boolean;
}

export interface ProcessSession {
  gamePath?: string;
  titleName?: string;
  phase: 'idle' | 'launching' | 'running' | 'exited' | 'failed';
  startedAt?: string;
  endedAt?: string;
  exitCode?: number;
  message?: string;
  tracking: 'host-events' | 'launch-only' | 'none';
}

export interface LauncherSettings {
  schemaVersion: 2;
  gameDirectories: string[];
  global: EmulatorSettings;
  perGame: Record<string, EmulatorSettings>;
  compatibility: Record<string, CompatibilityEntry>;
  patchSelections: Record<string, Record<string, boolean>>;
  library: {
    sortField: LibrarySortField;
    sortDirection: SortDirection;
  };
}

export const DEFAULT_EMULATOR_SETTINGS: EmulatorSettings = {
  screenResolution: '1280x720',
  vblankFrequency: 60,
  vulkanValidation: true,
  shaderValidation: true,
  shaderOptimization: 'Performance',
  shaderLogDirection: 'Silent',
  shaderLogFolder: '_Shaders',
  commandBufferDump: false,
  commandBufferDumpFolder: '_Buffers',
  printfDirection: 'Silent',
  printfOutputFile: '_prosperismo.txt',
  profilerDirection: 'None',
  renderDoc: false,
  nggRectlistDraw: true,
};

export const DEFAULT_LAUNCHER_SETTINGS: LauncherSettings = {
  schemaVersion: 2,
  gameDirectories: [],
  global: {...DEFAULT_EMULATOR_SETTINGS},
  perGame: {},
  compatibility: {},
  patchSelections: {},
  library: {sortField: 'titleName', sortDirection: 'ascending'},
};

export const DEFAULT_PROCESS_SESSION: ProcessSession = {
  phase: 'idle',
  tracking: 'none',
};
