import {DeviceEventEmitter, NativeModules} from 'react-native';
import type {
  DestructiveDirectoryRequest,
  DirectoryEntry,
  HostProcessEvent,
  LaunchRequest,
  ProsperismoHostGateway,
} from '../core/host';

interface NativeProsperismoHost {
  listDirectory(path: string): Promise<DirectoryEntry[]>;
  readTextFile(path: string): Promise<string>;
  readBinaryFile(path: string): Promise<number[]>;
  writeTextFile(path: string, contents: string): Promise<void>;
  canonicalizePath(path: string): Promise<string>;
  chooseGameDirectories(): Promise<string[]>;
  loadLauncherSettings(): Promise<string | null>;
  saveLauncherSettings(json: string): Promise<void>;
  findEmulator(): Promise<string>;
  fileExists(path: string): Promise<boolean>;
  resolveShellAssets(): Promise<ShellAssetPaths>;
  playAt9(path: string, loop: boolean, gain: number): Promise<void>;
  stopAt9(): Promise<void>;
  getStartupRoute(): Promise<'desktop' | 'big-picture'>;
  setBigPictureMode(enabled: boolean): Promise<void>;
  openPath(path: string): Promise<void>;
  removeDirectories(paths: string[], titleId: string, confirmed: boolean): Promise<string[]>;
  launch(executable: string, args: string[], workingDirectory: string): Promise<void>;
}

export interface ShellAssetPaths {
  oracleRoot: string;
  firmwareRoot: string;
  ui3Rco: string;
  baseRco: string;
  bgLayerRco: string;
  npxs40087Eboot: string;
  particle0Gnf: string;
  particle1Gnf: string;
  homeSource: string;
  settingsIcon: string;
  libraryIcon: string;
  desktopIcon: string;
  searchIcon: string;
  genericGameIcon: string;
  focusNoise: string;
  nativeDrawCache: string;
  nativeSequenceDirectory: string;
  coldBootChime: string;
  homeBgm: string;
}

const native = NativeModules.ProsperismoHost as NativeProsperismoHost | undefined;
const unavailable = (): Error =>
  new Error(
    'ProsperismoHost native module is not installed. Build the Windows host adapter described in src/native/README.md.',
  );

export const prosperismoHost: ProsperismoHostGateway = {
  listDirectory: path => native?.listDirectory(path) ?? Promise.reject(unavailable()),
  readTextFile: path => native?.readTextFile(path) ?? Promise.reject(unavailable()),
  canonicalizePath: path => native?.canonicalizePath(path) ?? Promise.resolve(path),
  chooseGameDirectories: () =>
    native?.chooseGameDirectories() ?? Promise.reject(unavailable()),
  loadLauncherSettings: () =>
    native?.loadLauncherSettings() ?? Promise.resolve(null),
  saveLauncherSettings: json =>
    native?.saveLauncherSettings(json) ?? Promise.reject(unavailable()),
  findEmulator: () => native?.findEmulator() ?? Promise.reject(unavailable()),
  fileExists: path => native?.fileExists(path) ?? Promise.resolve(false),
  launch: (request: LaunchRequest) =>
    native?.launch(request.executable, request.args, request.workingDirectory) ??
    Promise.reject(unavailable()),
  ...(native
    ? {
        writeTextFile: (path: string, contents: string) =>
          native.writeTextFile(path, contents),
        readBinaryFile: async (path: string) =>
          Uint8Array.from(await native.readBinaryFile(path)),
        openPath: (path: string) => native.openPath(path),
        removeDirectories: (request: DestructiveDirectoryRequest) =>
          native.removeDirectories(request.paths, request.titleId, request.confirmed),
        subscribeProcessEvents: (listener: (event: HostProcessEvent) => void) => {
          const subscription = DeviceEventEmitter.addListener(
            'ProsperismoHostProcess',
            listener,
          );
          return () => subscription.remove();
        },
      }
    : {}),
};

export const hasNativeProsperismoHost = Boolean(native);

export const setBigPictureMode = (enabled: boolean): Promise<void> =>
  native?.setBigPictureMode(enabled) ?? Promise.resolve();

export const getStartupRoute = (): Promise<'desktop' | 'big-picture'> =>
  native?.getStartupRoute() ?? Promise.resolve('desktop');

export const resolveShellAssets = (): Promise<ShellAssetPaths | undefined> =>
  native?.resolveShellAssets() ?? Promise.resolve(undefined);

export const playAt9 = (path: string | undefined, loop: boolean, gain = 1): Promise<void> =>
  path ? native?.playAt9(path, loop, gain) ?? Promise.resolve() : stopAt9();

export const stopAt9 = (): Promise<void> => native?.stopAt9() ?? Promise.resolve();
