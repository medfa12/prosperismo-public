/**
 * Host-side contract of HOME's embedded Game Hub boundary, recovered from
 * NPXS40002 m351/m505/m512 and NPXS40033 m728 (see
 * docs/sony-shell/ps5-hub-and-cards.md). This is the app-module/channel
 * adapter's data plane: URI identity, pool keying, per-experience state, and
 * Sony's AppBrowse key encoders. No guest module executes yet — these types
 * describe the boundary; they do not fake its content.
 */

export const SHELL_HUB_MODULE_PROTOCOL = {
  poolSize: 4,
  showDelayMs: 260,
  reclaimDelayMs: 60_000,
  debounceDelayMs: 300,
  /** Protocol identifiers; the JavaScript spelling is part of the contract. */
  noResponseCallbacks: [
    'focusReady',
    'onTemplateChange',
    'setBackgroundImage',
    'setBackgroundMusic',
    'toggleHeader',
  ],
} as const;

export interface ShellHubModuleConfig {
  hubUri: string;
  /** `scheme:path` — the native AppModule pool key. */
  appModulePath: string;
  appModuleName: string;
  appModuleUrl: string;
  channelTopic: string;
  /** The original query; it travels over the provider channel, not the URL. */
  queryParams: Readonly<Record<string, string | null>>;
}

/**
 * Key used inside the guest by HubAppContextProvider. Unlike the native pool
 * key this changes with the original query and remounts the provider subtree.
 */
export function hubGuestContextKey(config: ShellHubModuleConfig): string {
  return JSON.stringify(config.queryParams);
}

export function hubModulesShareNativeSlot(a: ShellHubModuleConfig, b: ShellHubModuleConfig): boolean {
  return a.appModulePath === b.appModulePath;
}

function isScheme(value: string): boolean {
  return /^[A-Za-z][A-Za-z0-9+\-.]*$/.test(value);
}

function decodeQueryPart(value: string): string {
  try {
    return decodeURIComponent(value.replace(/\+/g, ' '));
  } catch {
    return value.replace(/\+/g, ' ');
  }
}

function parseHubQuery(hubUri: string, queryStart: number, fragmentStart: number): Record<string, string | null> {
  const values: Record<string, string | null> = {};
  if (queryStart < 0 || (fragmentStart >= 0 && fragmentStart < queryStart)) {
    return values;
  }
  const end = fragmentStart >= 0 ? fragmentStart : hubUri.length;
  for (const pair of hubUri.slice(queryStart + 1, end).split('&')) {
    if (!pair) {
      continue;
    }
    const equals = pair.indexOf('=');
    const key = decodeQueryPart(equals >= 0 ? pair.slice(0, equals) : pair);
    if (key.length > 0) {
      values[key] = equals >= 0 ? decodeQueryPart(pair.slice(equals + 1)) : null;
    }
  }
  return values;
}

/**
 * `useHubConfig(hubUri)`: the module and channel identity is `scheme:path`;
 * the query is carried separately and a query-only change keeps the native
 * module while remounting the guest context subtree.
 */
export function parseHubUri(hubUri: string | undefined): ShellHubModuleConfig | undefined {
  if (!hubUri || !hubUri.trim()) {
    return undefined;
  }
  const colon = hubUri.indexOf(':');
  if (colon <= 0 || colon === hubUri.length - 1) {
    return undefined;
  }
  const scheme = hubUri.slice(0, colon);
  if (!isScheme(scheme)) {
    return undefined;
  }
  const queryStart = hubUri.indexOf('?', colon + 1);
  const fragmentStart = hubUri.indexOf('#', colon + 1);
  let pathEnd = hubUri.length;
  if (queryStart >= 0) {
    pathEnd = queryStart;
  }
  if (fragmentStart >= 0 && fragmentStart < pathEnd) {
    pathEnd = fragmentStart;
  }
  const path = hubUri.slice(colon + 1, pathEnd);
  if (!path) {
    return undefined;
  }
  const appModulePath = `${scheme}:${path}`;
  return {
    hubUri,
    appModulePath,
    appModuleName: `app-module-${appModulePath}`,
    appModuleUrl: `${appModulePath}?isFromHubSDK=1`,
    channelTopic: `channel-${appModulePath}`,
    queryParams: parseHubQuery(hubUri, queryStart, fragmentStart),
  };
}

/**
 * Mutable state retained for one experience id (HOME m512): readiness,
 * background image/music and both vertical offsets survive per title even
 * when several titles share one pooled `scheme:path` module slot. Callback
 * payload schemas belong to the guest, so they are preserved opaquely.
 */
export class ShellHubExperienceState {
  private readyValue = false;
  private backgroundImageValue: unknown = null;
  private backgroundMusicValue: unknown = null;
  private homeOffsetValue = 0;
  private hubOffsetValue = 0;

  get isReady(): boolean { return this.readyValue; }
  get backgroundImage(): unknown { return this.backgroundImageValue; }
  get backgroundMusic(): unknown { return this.backgroundMusicValue; }
  get homeOffset(): number { return this.homeOffsetValue; }
  get hubOffset(): number { return this.hubOffsetValue; }

  /** One-shot; returns true only on the not-ready → ready transition. */
  focusReady(): boolean {
    const changed = !this.readyValue;
    this.readyValue = true;
    return changed;
  }

  /** Accepts the callback's single two-element array; rejects malformed payloads. */
  onTemplateChange(offsets: readonly number[] | undefined): boolean {
    if (!offsets || offsets.length !== 2
      || !Number.isFinite(offsets[0]) || !Number.isFinite(offsets[1])) {
      return false;
    }
    this.homeOffsetValue = offsets[0];
    this.hubOffsetValue = offsets[1];
    return true;
  }

  setBackgroundImage(payload: unknown): void { this.backgroundImageValue = payload; }
  setBackgroundMusic(payload: unknown): void { this.backgroundMusicValue = payload; }

  unload(): void {
    this.readyValue = false;
    this.backgroundImageValue = null;
    this.backgroundMusicValue = null;
    this.homeOffsetValue = 0;
    this.hubOffsetValue = 0;
  }
}

const SCP_CONCEPT_PREFIX = 'cid:scp:';
const LOCAL_CONCEPT_PREFIX = 'cid:local:';
const GAME_HUB_PREFIX = 'pshome:gamehub?titleId=';
const ULONG_MAX = 0xffff_ffff_ffff_ffffn;

/**
 * Sony's `conceptIdToAppDbKey`: a positive decimal concept id becomes a
 * lower-case sixteen-digit hexadecimal SCP key.
 */
export function conceptIdToAppDbKey(conceptId: string | undefined): string | undefined {
  const trimmed = conceptId?.trim();
  if (!trimmed || !/^\d+$/.test(trimmed)) {
    return undefined;
  }
  const numeric = BigInt(trimmed);
  if (numeric === 0n || numeric > ULONG_MAX) {
    return undefined;
  }
  return SCP_CONCEPT_PREFIX + numeric.toString(16).padStart(16, '0');
}

/**
 * Sony's `titleIdToAppDbKey`: NP title ids lose their first `_00` suffix
 * before receiving the local-concept prefix.
 */
export function titleIdToAppDbKey(titleId: string | undefined): string | undefined {
  const trimmed = titleId?.trim();
  if (!trimmed) {
    return undefined;
  }
  const npSuffix = trimmed.indexOf('_00');
  const normalized = npSuffix > 0
    ? trimmed.slice(0, npSuffix) + trimmed.slice(npSuffix + 3)
    : trimmed;
  return LOCAL_CONCEPT_PREFIX + normalized;
}

/** AppDb's normal installed-game route (the embedded HOME `pshome:gamehub`). */
export function ordinaryGameHubUri(titleId: string | undefined): string | undefined {
  const trimmed = titleId?.trim();
  return trimmed ? GAME_HUB_PREFIX + encodeURIComponent(trimmed) : undefined;
}

export interface ShellAppBrowseMetadata {
  experienceId?: string;
  hubUri?: string;
}

/**
 * Normalizes a game package into the two fields HOME obtains from AppBrowse.
 * A package-authored hub URI wins; concept-backed identity wins over the
 * local-title fallback.
 */
export function appBrowseMetadataFromPackage(
  titleId: string | undefined,
  conceptId?: string,
  explicitHubAppUri?: string,
): ShellAppBrowseMetadata {
  return {
    experienceId: conceptIdToAppDbKey(conceptId) ?? titleIdToAppDbKey(titleId),
    hubUri: explicitHubAppUri?.trim() || ordinaryGameHubUri(titleId),
  };
}
