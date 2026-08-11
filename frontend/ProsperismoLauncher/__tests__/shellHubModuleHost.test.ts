import {
  appBrowseMetadataFromPackage,
  conceptIdToAppDbKey,
  hubGuestContextKey,
  hubModulesShareNativeSlot,
  ordinaryGameHubUri,
  parseHubUri,
  SHELL_HUB_MODULE_PROTOCOL,
  ShellHubExperienceState,
  titleIdToAppDbKey,
} from '../src/bigPicture/shellHubModuleHost';

describe('hub app-module protocol (NPXS40002 m351/m505, NPXS40033 m728)', () => {
  it('pins the pool size, timings, and no-response callback names', () => {
    expect(SHELL_HUB_MODULE_PROTOCOL.poolSize).toBe(4);
    expect(SHELL_HUB_MODULE_PROTOCOL.showDelayMs).toBe(260);
    expect(SHELL_HUB_MODULE_PROTOCOL.reclaimDelayMs).toBe(60000);
    expect(SHELL_HUB_MODULE_PROTOCOL.debounceDelayMs).toBe(300);
    expect(SHELL_HUB_MODULE_PROTOCOL.noResponseCallbacks).toEqual([
      'focusReady',
      'onTemplateChange',
      'setBackgroundImage',
      'setBackgroundMusic',
      'toggleHeader',
    ]);
  });

  it('derives module, url and channel identity from scheme:path', () => {
    const config = parseHubUri('pshome:gamehub?titleId=PPSA01234&flag');
    expect(config).toBeDefined();
    expect(config?.appModulePath).toBe('pshome:gamehub');
    expect(config?.appModuleName).toBe('app-module-pshome:gamehub');
    expect(config?.appModuleUrl).toBe('pshome:gamehub?isFromHubSDK=1');
    expect(config?.channelTopic).toBe('channel-pshome:gamehub');
    expect(config?.queryParams).toEqual({titleId: 'PPSA01234', flag: null});
  });

  it('keeps the native slot across query-only changes but remounts the guest key', () => {
    const first = parseHubUri('pshome:gamehub?titleId=PPSA01234');
    const second = parseHubUri('pshome:gamehub?titleId=PPSA09999');
    expect(first && second && hubModulesShareNativeSlot(first, second)).toBe(true);
    expect(hubGuestContextKey(first!)).not.toBe(hubGuestContextKey(second!));
  });

  it('rejects malformed hub uris', () => {
    expect(parseHubUri(undefined)).toBeUndefined();
    expect(parseHubUri('')).toBeUndefined();
    expect(parseHubUri('no-colon-here')).toBeUndefined();
    expect(parseHubUri(':path')).toBeUndefined();
    expect(parseHubUri('scheme:')).toBeUndefined();
    expect(parseHubUri('1bad:path')).toBeUndefined();
    expect(parseHubUri('psgamehub:#fragment')).toBeUndefined();
  });

  it('excludes fragments and decodes plus and percent escapes in the query', () => {
    const config = parseHubUri('scheme:path?a=1+2&b=%2Fx#c=3');
    expect(config?.queryParams).toEqual({a: '1 2', b: '/x'});
    const fragmentBeforeQuery = parseHubUri('scheme:path#frag?a=1');
    expect(fragmentBeforeQuery?.appModulePath).toBe('scheme:path');
    expect(fragmentBeforeQuery?.queryParams).toEqual({});
  });
});

describe('per-experience hub state (HOME m512)', () => {
  it('treats focusReady as a one-shot transition', () => {
    const state = new ShellHubExperienceState();
    expect(state.isReady).toBe(false);
    expect(state.focusReady()).toBe(true);
    expect(state.focusReady()).toBe(false);
    expect(state.isReady).toBe(true);
  });

  it('accepts only a finite two-element offsets payload', () => {
    const state = new ShellHubExperienceState();
    expect(state.onTemplateChange(undefined)).toBe(false);
    expect(state.onTemplateChange([1])).toBe(false);
    expect(state.onTemplateChange([1, Number.NaN])).toBe(false);
    expect(state.homeOffset).toBe(0);
    expect(state.onTemplateChange([8, 0])).toBe(true);
    expect(state.homeOffset).toBe(8);
    expect(state.hubOffset).toBe(0);
  });

  it('unload clears readiness, payloads and offsets', () => {
    const state = new ShellHubExperienceState();
    state.focusReady();
    state.onTemplateChange([-50, 0]);
    state.setBackgroundImage({uri: 'x'});
    state.setBackgroundMusic({uri: 'y'});
    state.unload();
    expect(state.isReady).toBe(false);
    expect(state.backgroundImage).toBeNull();
    expect(state.backgroundMusic).toBeNull();
    expect(state.homeOffset).toBe(0);
    expect(state.hubOffset).toBe(0);
  });
});

describe('AppBrowse identity encoders', () => {
  it('encodes a decimal concept id as a sixteen-digit hex scp key', () => {
    expect(conceptIdToAppDbKey('10002358')).toBe('cid:scp:0000000000989fb6');
    expect(conceptIdToAppDbKey(' 1 ')).toBe('cid:scp:0000000000000001');
    expect(conceptIdToAppDbKey('0')).toBeUndefined();
    expect(conceptIdToAppDbKey('-5')).toBeUndefined();
    expect(conceptIdToAppDbKey('not-a-number')).toBeUndefined();
    expect(conceptIdToAppDbKey('18446744073709551615')).toBe('cid:scp:ffffffffffffffff');
    expect(conceptIdToAppDbKey('18446744073709551616')).toBeUndefined();
  });

  it('drops the first _00 suffix from NP title ids for the local key', () => {
    expect(titleIdToAppDbKey('PPSA01234_00')).toBe('cid:local:PPSA01234');
    expect(titleIdToAppDbKey('PPSA01234')).toBe('cid:local:PPSA01234');
    expect(titleIdToAppDbKey(undefined)).toBeUndefined();
    expect(titleIdToAppDbKey('  ')).toBeUndefined();
  });

  it('builds the ordinary pshome:gamehub route and prefers package metadata', () => {
    expect(ordinaryGameHubUri('PPSA01234')).toBe('pshome:gamehub?titleId=PPSA01234');
    const conceptBacked = appBrowseMetadataFromPackage('PPSA01234_00', '10002358');
    expect(conceptBacked.experienceId).toBe('cid:scp:0000000000989fb6');
    expect(conceptBacked.hubUri).toBe('pshome:gamehub?titleId=PPSA01234_00');
    const localFallback = appBrowseMetadataFromPackage('PPSA01234_00');
    expect(localFallback.experienceId).toBe('cid:local:PPSA01234');
    const explicit = appBrowseMetadataFromPackage('PPSA01234', undefined, ' psgamehub:main ');
    expect(explicit.hubUri).toBe('psgamehub:main');
  });
});
