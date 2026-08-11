export interface DirectoryEntry {
  name: string;
  path: string;
  kind: 'file' | 'directory';
  symbolicLink?: boolean;
}

export interface LaunchRequest {
  executable: string;
  args: string[];
  workingDirectory: string;
}

export interface HostProcessEvent {
  phase: 'running' | 'exited' | 'failed';
  exitCode?: number;
  message?: string;
}

export interface DestructiveDirectoryRequest {
  /** Exact, pre-resolved paths displayed to and confirmed by the user. */
  paths: string[];
  titleId: string;
  confirmed: true;
}

export interface ProsperismoHostGateway {
  listDirectory(path: string): Promise<DirectoryEntry[]>;
  readTextFile(path: string): Promise<string>;
  canonicalizePath(path: string): Promise<string>;
  chooseGameDirectories(): Promise<string[]>;
  loadLauncherSettings(): Promise<string | null>;
  saveLauncherSettings(json: string): Promise<void>;
  findEmulator(): Promise<string>;
  fileExists(path: string): Promise<boolean>;
  launch(request: LaunchRequest): Promise<void>;
  /** Optional until the Windows bridge advertises the capability. */
  writeTextFile?(path: string, contents: string): Promise<void>;
  readBinaryFile?(path: string): Promise<Uint8Array>;
  openPath?(path: string): Promise<void>;
  removeDirectories?(request: DestructiveDirectoryRequest): Promise<string[]>;
  subscribeProcessEvents?(listener: (event: HostProcessEvent) => void): () => void;
}
