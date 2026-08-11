import React, {useCallback, useEffect, useMemo, useReducer, useRef, useState} from 'react';
import {
  Animated,
  Easing,
  findNodeHandle,
  StyleSheet,
  Text,
  UIManager,
  useWindowDimensions,
  View,
} from 'react-native';
import type {GameInstall, LauncherSettings} from '../core/models';
import {INITIAL_SHELL_STATE, reduceShellState, selectedShellGame, type ShellDirection} from './shellState';
import {ShellHubReadiness} from './shellHubTransition';
import {appBrowseMetadataFromPackage} from './shellHubModuleHost';
import {SHELL_METRICS} from './shellMetrics';
import {RecoveredHomeShell} from './RecoveredHomeShell';
import {ShellBackgroundSurface} from './ShellBackgroundSurface';
import {shellCardMedia} from './shellCardMedia';
import {SHELL_COLD_BOOT_DURATION_MS} from './shellBackgroundSequence';
import {playAt9, stopAt9} from '../native/ProsperismoHost';
import {ShellAllGamesSurface} from './ShellLibrarySurface';
import {shellTextStyle} from './shellTypography';
import {
  PROSPERISMO_SETTINGS_CATEGORIES,
  ProsperismoSettingsDetail,
  ProsperismoSettingsRoot,
} from './ProsperismoSettingsSurface';
import {
  ProfileMenu,
  SearchSurface,
  ShellButtonPrompts,
  ShellContextMenu,
  ShellDialogSurface,
} from './ShellUtilitySurfaces';

const SYSTEM_ACTIONS = [
  {label: 'Search', glyph: 'search'},
  {label: 'Settings', glyph: 'settings'},
  {label: 'Profile', glyph: 'profile'},
] as const;

export interface FirmwareShellIconPaths {
  settings?: string;
  library?: string;
  desktop?: string;
  search?: string;
  genericGame?: string;
  focusNoise?: string;
}

export interface FirmwareShellMediaPaths {
  nativeSequenceDirectory?: string;
  coldBootChime?: string;
  homeBgm?: string;
}

function formatClock(now: Date): string {
  return now.toLocaleTimeString([], {hour: 'numeric', minute: '2-digit'});
}

function homeDirectionForKey(key: string | undefined): ShellDirection | undefined {
  if (key === 'ArrowLeft' || key === 'GamepadDPadLeft') { return 'left'; }
  if (key === 'ArrowRight' || key === 'GamepadDPadRight') { return 'right'; }
  if (key === 'ArrowUp' || key === 'GamepadDPadUp') { return 'up'; }
  if (key === 'ArrowDown' || key === 'GamepadDPadDown') { return 'down'; }
  return undefined;
}

type FocusableUIManager = typeof UIManager & {focus?(reactTag: number): void};
function OptionsModal({game, selectedIndex, onClose, onPlay, onSelect, onUnavailable}: {
  game: GameInstall;
  selectedIndex: number;
  onClose(): void;
  onPlay(): void;
  onSelect(index: number): void;
  onUnavailable(label: string): void;
}) {
  return <ShellContextMenu
    anchor={{x: 172, y: 126, width: 168, height: 168}}
    entries={[
      {kind: 'header', id: 'game', label: game.titleName},
      {kind: 'action', id: 'play', label: 'Play Game', glyph: '▶', onPress: onPlay},
      {kind: 'action', id: 'settings', label: 'Game Settings', glyph: '⚙', onPress: () => onUnavailable('Game Settings')},
      {kind: 'action', id: 'folder', label: 'Open Game Folder', glyph: '□', onPress: () => onUnavailable('Open Game Folder')},
      {kind: 'separator', id: 'separator'},
      {kind: 'action', id: 'copy-id', label: `Copy Title ID${game.titleId ? `  ${game.titleId}` : ''}`, glyph: '⧉', onPress: () => onUnavailable('Copy Title ID')},
      {kind: 'action', id: 'remove', label: 'Remove from Library', glyph: '−', destructive: true, onPress: () => onUnavailable('Remove from Library')},
    ]}
    onClose={onClose}
    onSelect={onSelect}
    selectedIndex={selectedIndex}
  />;
}

function ShellToast({message, onClose}: {message: string; onClose(): void}) {
  const phase = useRef(new Animated.Value(0)).current;
  useEffect(() => {
    const animation = Animated.sequence([
      Animated.timing(phase, {toValue: 1, duration: 300, easing: Easing.linear, useNativeDriver: true}),
      Animated.delay(3500),
      Animated.timing(phase, {toValue: 0, duration: 200, easing: Easing.linear, useNativeDriver: true}),
    ]);
    animation.start(({finished}) => { if (finished) { onClose(); } });
    return () => animation.stop();
  }, [onClose, phase]);
  return <Animated.View pointerEvents="none" style={[shellStyles.toast, {opacity: phase}]}><View style={shellStyles.toastIcon}><View style={shellStyles.toastIconMark} /></View><Text numberOfLines={2} style={shellStyles.toastText}>{message}</Text></Animated.View>;
}

/**
 * The action-card host uses a 764 x 440 dialog with a 676px body, 44px side
 * margins, a 40px message icon, and 388px text buttons. Keep the emulator
 * error inside the controller shell rather than falling back to Desktop.
 */
function ShellDialog({title, message, onDismiss}: {title: string; message: string; onDismiss(): void}) {
  return <ShellDialogSurface
    body={message}
    buttons={[{id: 'ok', label: 'OK', onPress: onDismiss}]}
    onDismiss={onDismiss}
    title={title}
  />;
}

export interface BigPictureShellProps {
  games: readonly GameInstall[];
  firmwareShellIcons?: FirmwareShellIconPaths;
  firmwareShellMedia?: FirmwareShellMediaPaths;
  playColdBoot?: boolean;
  onColdBootComplete?(): void;
  emulatorRunning?: boolean;
  settings: LauncherSettings;
  onSaveSettings(next: LauncherSettings): void;
  onAddFolders?(): void;
  onDesktop(): void;
  onLaunch(game: GameInstall): void;
  errorMessage?: string;
  onDismissError(): void;
}

export function BigPictureShell({games, firmwareShellIcons = {}, firmwareShellMedia = {}, playColdBoot = true, onColdBootComplete, emulatorRunning = false, settings, onSaveSettings, onAddFolders, onDesktop, onLaunch, errorMessage, onDismissError}: BigPictureShellProps) {
  const [state, dispatch] = useReducer(reduceShellState, INITIAL_SHELL_STATE);
  const [now, setNow] = useState(() => new Date());
  const [optionsGame, setOptionsGame] = useState<GameInstall>();
  const [optionIndex, setOptionIndex] = useState(1);
  const [settingsDetail, setSettingsDetail] = useState<number>();
  const [searchOpen, setSearchOpen] = useState(false);
  const [profileOpen, setProfileOpen] = useState(false);
  const [toast, setToast] = useState<string>();
  const [coldBootActive, setColdBootActive] = useState(playColdBoot);
  const dismissToast = useCallback(() => setToast(undefined), []);
  // One-shot focusReady registry (m503). No executing hub app module exists
  // yet, so every experience stays unready and Down on a tile is swallowed —
  // the recovered pre-boot behaviour. The future app-module adapter marks
  // readiness here; nothing else may.
  const hubReadiness = useRef(new ShellHubReadiness()).current;
  const spaceRefs = useRef<any[]>([]);
  const strandRefs = useRef<any[]>([]);
  const systemRefs = useRef<any[]>([]);
  const libraryRef = useRef<any>(undefined);
  const settingsRefs = useRef<any[]>([]);
  const {width, height} = useWindowDimensions();
  const scale = Math.min(width / SHELL_METRICS.canvas.width, height / SHELL_METRICS.canvas.height);
  const selected = selectedShellGame(games, state);
  const shellGames = useMemo(() => games.slice(0, SHELL_METRICS.strand.maxItems), [games]);
  useEffect(() => { const timer = setInterval(() => setNow(new Date()), 30000); return () => clearInterval(timer); }, []);
  useEffect(() => {
    if (!coldBootActive) {
      return;
    }
    playAt9(firmwareShellMedia.coldBootChime, false, 8);
    const timer = setTimeout(() => {
      setColdBootActive(false);
      onColdBootComplete?.();
    }, SHELL_COLD_BOOT_DURATION_MS);
    return () => clearTimeout(timer);
  }, [coldBootActive, firmwareShellMedia.coldBootChime, onColdBootComplete]);
  const focusNative = (target: any) => {
    if (typeof target?.focus === 'function') {
      target.focus();
      return;
    }
    const tag = findNodeHandle(target);
    const manager = UIManager as FocusableUIManager;
    if (tag !== null && typeof manager.focus === 'function') {
      manager.focus(tag);
    }
  };
  const launch = (game: GameInstall) => { setOptionsGame(undefined); setToast(`Launching ${game.titleName}`); stopAt9(); onLaunch(game); };
  const openOptions = (game: GameInstall) => { setOptionIndex(1); setOptionsGame(game); };
  const unavailableOption = (label: string) => { setOptionsGame(undefined); setToast(`${label} is available in Desktop Mode`); };
  useEffect(() => {
    if (optionsGame || errorMessage || settingsDetail !== undefined) {
      return;
    }
    if (state.surface === 'settings') {
      focusNative(settingsRefs.current[state.settingsIndex]);
      return;
    }
    if (state.surface === 'home') {
      if (state.focusRegion === 'system') {
        focusNative(systemRefs.current[Math.min(state.systemIndex, SYSTEM_ACTIONS.length - 1)]);
      } else if (state.focusRegion === 'library-shortcut') {
        focusNative(libraryRef.current);
      } else if (state.focusRegion === 'spaces' || shellGames.length === 0) {
        focusNative(spaceRefs.current[state.spaceCursor === 'games' ? 0 : 1]);
      } else {
        focusNative(strandRefs.current[Math.min(state.selectedIndex, shellGames.length - 1)]);
      }
    }
  }, [errorMessage, optionsGame, settingsDetail, shellGames.length, state.focusRegion, state.selectedIndex, state.settingsIndex, state.spaceCursor, state.surface, state.systemIndex]);
  const handleKeyDown = (event: any) => {
    const key = event?.nativeEvent?.key;
    if (coldBootActive) {
      event.stopPropagation?.();
      return;
    }
    if (searchOpen || profileOpen) {
      return;
    }
    if (errorMessage) {
      if (key === 'Escape' || key === 'GamepadB' || key === 'Enter' || key === 'GamepadA') { onDismissError(); }
      event.stopPropagation?.();
      return;
    }
    if (state.surface === 'library') {
      return;
    }
    if (state.surface === 'home' && selected && (key === 'GamepadMenu' || key === 'ContextMenu' || key === 'F10')) {
      openOptions(selected);
      event.stopPropagation?.();
      return;
    }
    const homeDirection = homeDirectionForKey(key);
    if (state.surface === 'home' && state.verticalPosition === 'hub'
      && (key === 'Escape' || key === 'GamepadB' || homeDirection === 'up')) {
      dispatch({type: 'ascend-home'});
      event.stopPropagation?.();
      return;
    }
    if (state.surface === 'home' && homeDirection) {
      if (homeDirection === 'down' && state.focusRegion === 'strand' && state.verticalPosition === 'home') {
        // Readiness is keyed by the AppBrowse experience id (cid:scp: from a
        // package concept id, else the cid:local: title key), never the raw
        // title id or path.
        const experienceId = appBrowseMetadataFromPackage(selected?.titleId).experienceId;
        dispatch({type: 'descend-hub', hubReady: hubReadiness.isReady(experienceId)});
      } else {
        dispatch({type: 'navigate-home', direction: homeDirection, gameCount: shellGames.length, systemCount: SYSTEM_ACTIONS.length});
      }
      event.stopPropagation?.();
      return;
    }
    if (key === 'ArrowUp' || key === 'GamepadDPadUp') { if (state.focusRegion === 'content' && state.surface === 'settings' && settingsDetail === undefined) { if (state.settingsIndex > 0) { focusNative(settingsRefs.current[state.settingsIndex - 1]); } else { dispatch({type: 'home'}); focusNative(strandRefs.current[state.selectedIndex]); } } else if (state.focusRegion === 'content' && settingsDetail === undefined) { dispatch({type: 'home'}); focusNative(strandRefs.current[state.selectedIndex]); } event.stopPropagation?.(); return; }
    if (key === 'ArrowDown' || key === 'GamepadDPadDown') { if (state.focusRegion === 'content' && state.surface === 'settings' && settingsDetail === undefined && state.settingsIndex < PROSPERISMO_SETTINGS_CATEGORIES.length - 1) { focusNative(settingsRefs.current[state.settingsIndex + 1]); event.stopPropagation?.(); return; } }
    if (key === 'Escape' || key === 'GamepadB') { if (optionsGame) { setOptionsGame(undefined); } else if (settingsDetail !== undefined) { setSettingsDetail(undefined); } else if (state.surface !== 'home') { dispatch({type: 'home'}); } event.stopPropagation?.(); }
  };
  // React Native Windows exposes this event at runtime, while the shared RN
  // declaration used by this project does not include the Windows extension.
  const windowsKeyCapture = {onKeyDownCapture: handleKeyDown} as any;
  const modalOpen = searchOpen || profileOpen || Boolean(optionsGame) || Boolean(errorMessage);
  const cardMedia = shellCardMedia(selected, state, modalOpen);
  useEffect(() => {
    if (coldBootActive || emulatorRunning) {
      if (emulatorRunning) {
        stopAt9();
      }
      return;
    }
    const path = cardMedia.musicPath ?? firmwareShellMedia.homeBgm;
    const delay = cardMedia.musicPath ? 120 : 0;
    const timer = setTimeout(() => { playAt9(path, true, cardMedia.musicPath ? 1 : 8); }, delay);
    return () => clearTimeout(timer);
  }, [cardMedia.musicPath, coldBootActive, emulatorRunning, firmwareShellMedia.homeBgm]);
  useEffect(() => () => { stopAt9(); }, []);
  // The recovered plate transition ripples out of the focused item, so hand
  // the background owner the focused strand card's bounds. HOME's focused
  // card is 168x168 at (172, 126) in the fixed 1920x1080 design space.
  const focusedCardRect = state.surface === 'home' && state.focusRegion === 'strand' && selected
    ? {
        x: SHELL_METRICS.strand.left,
        y: SHELL_METRICS.strand.top,
        width: SHELL_METRICS.strand.focusedSize,
        height: SHELL_METRICS.strand.focusedSize,
      }
    : undefined;
  const persistentBackground = <ShellBackgroundSurface
    artworkPath={coldBootActive ? undefined : cardMedia.artworkPath}
    coldBootActive={coldBootActive}
    focusedRect={focusedCardRect}
    modalOpen={modalOpen}
    nativeSequenceDirectory={firmwareShellMedia.nativeSequenceDirectory}
    surface={state.surface}
  />;
  if (state.surface === 'home') {
    return <View style={shellStyles.viewport} {...windowsKeyCapture}>
      {persistentBackground}
      <View pointerEvents={coldBootActive ? 'none' : 'box-none'} style={[shellStyles.uiOwner, coldBootActive && shellStyles.uiHidden]}>
      <RecoveredHomeShell
        clock={formatClock(now)}
        focusRegion={state.focusRegion}
        verticalPosition={state.verticalPosition}
        games={shellGames}
        libraryIconPath={firmwareShellIcons.library}
        genericGameIconPath={firmwareShellIcons.genericGame}
        onActivateSystem={action => {
          if (action === 'search') {
            setSearchOpen(true);
          } else if (action === 'settings') {
            dispatch({type: 'open-settings'});
          } else {
            setProfileOpen(true);
          }
        }}
        onLaunch={launch}
        onFocusLibrary={() => dispatch({type: 'focus', region: 'library-shortcut'})}
        onOpenLibrary={() => dispatch({type: 'open-library'})}
        onOptions={openOptions}
        onSelectGame={index => dispatch({type: 'select-game', index, gameCount: shellGames.length})}
        onFocusSpace={space => dispatch({type: 'focus-space', space})}
        onActivateSpace={space => dispatch({type: 'set-space', space})}
        onSelectSystem={index => dispatch({type: 'select-system', index})}
        selectedIndex={Math.min(state.selectedIndex, Math.max(0, shellGames.length - 1))}
        focusedSpace={state.spaceCursor}
        selectedSpace={state.space}
        selectedSystemIndex={state.systemIndex}
        settingsIconPath={firmwareShellIcons.settings}
        searchIconPath={firmwareShellIcons.search}
        libraryRef={libraryRef}
        spaceRefs={spaceRefs}
        strandRefs={strandRefs}
        systemRefs={systemRefs}
        viewportHeight={height}
        viewportWidth={width}
      />
      <View pointerEvents="box-none" style={[shellStyles.canvas, {transform: [{scale}]}]}>
        {!modalOpen && <ShellButtonPrompts prompts={selected
          ? [{kind: 'confirm', label: 'Select'}, {kind: 'options', label: 'Options'}]
          : [{kind: 'confirm', label: 'Select'}]}
        />}
        {optionsGame && <OptionsModal game={optionsGame} onSelect={setOptionIndex} selectedIndex={optionIndex} onClose={() => setOptionsGame(undefined)} onPlay={() => launch(optionsGame)} onUnavailable={unavailableOption} />}
        {searchOpen && <SearchSurface games={games} onClose={() => { setSearchOpen(false); focusNative(systemRefs.current[0]); }} onLaunch={game => { setSearchOpen(false); launch(game); }} />}
        {profileOpen && <ProfileMenu onClose={() => { setProfileOpen(false); focusNative(systemRefs.current[2]); }} onDesktop={() => { setProfileOpen(false); onDesktop(); }} />}
        {errorMessage && <ShellDialog title="Unable to start game" message={errorMessage} onDismiss={onDismissError} />}
        {toast && <ShellToast message={toast} onClose={dismissToast} />}
      </View>
      </View>
    </View>;
  }
  return <View style={shellStyles.viewport} {...windowsKeyCapture}>
    {persistentBackground}
    <View pointerEvents={coldBootActive ? 'none' : 'box-none'} style={[shellStyles.uiOwner, coldBootActive && shellStyles.uiHidden]}>
    <View style={[shellStyles.canvas, {transform: [{scale}]}]}>
    {state.surface === 'library' && <ShellAllGamesSurface
      games={games}
      initialDirection={settings.library.sortDirection}
      initialField={settings.library.sortField}
      onAddFolder={onAddFolders}
      onBack={() => dispatch({type: 'home'})}
      onLaunch={launch}
      onOptions={openOptions}
    />}
    {state.surface === 'settings' && settingsDetail === undefined && <ProsperismoSettingsRoot onRef={(index, node) => { settingsRefs.current[index] = node; }} onActivate={setSettingsDetail} onSelect={index => dispatch({type: 'select-setting', index})} selectedIndex={state.settingsIndex} />}
    {state.surface === 'settings' && settingsDetail !== undefined && <ProsperismoSettingsDetail categoryIndex={settingsDetail} onBack={() => setSettingsDetail(undefined)} onSave={onSaveSettings} settings={settings} />}
    {!searchOpen && !profileOpen && !errorMessage && state.surface !== 'library' && <ShellButtonPrompts prompts={[{kind: 'confirm', label: 'Select'}, {kind: 'back', label: 'Back'}]} />}
    {optionsGame && <OptionsModal game={optionsGame} onSelect={setOptionIndex} selectedIndex={optionIndex} onClose={() => setOptionsGame(undefined)} onPlay={() => launch(optionsGame)} onUnavailable={unavailableOption} />}
    {searchOpen && <SearchSurface games={games} onClose={() => { setSearchOpen(false); focusNative(systemRefs.current[0]); }} onLaunch={game => { setSearchOpen(false); launch(game); }} />}
    {profileOpen && <ProfileMenu onClose={() => { setProfileOpen(false); focusNative(systemRefs.current[2]); }} onDesktop={() => { setProfileOpen(false); onDesktop(); }} />}
    {errorMessage && <ShellDialog title="Unable to start game" message={errorMessage} onDismiss={onDismissError} />}
    {toast && <ShellToast message={toast} onClose={dismissToast} />}
  </View></View></View>;
}

const shellStyles = StyleSheet.create({
  viewport: {flex: 1, alignItems: 'center', justifyContent: 'center', backgroundColor: '#020408', overflow: 'hidden'},
  uiOwner: {...StyleSheet.absoluteFillObject, alignItems: 'center', justifyContent: 'center'},
  uiHidden: {opacity: 0},
  canvas: {position: 'absolute', width: 1920, height: 1080, backgroundColor: 'transparent'},
  toast: {position: 'absolute', alignSelf: 'center', bottom: 0, minWidth: 80, maxWidth: 652, minHeight: 72, paddingLeft: 20, paddingRight: 24, paddingVertical: 16, borderRadius: 20, flexDirection: 'row', alignItems: 'center', backgroundColor: 'rgba(255,255,255,0.04)'},
  toastIcon: {width: 40, height: 40, marginRight: 16, borderRadius: 20, alignItems: 'center', justifyContent: 'center', backgroundColor: 'rgba(255,255,255,0.08)'},
  toastIconMark: {width: 12, height: 12, borderRadius: 6, backgroundColor: '#fff'},
  toastText: {flexShrink: 1, color: '#fff', lineHeight: 22, ...shellTextStyle('Size3XSmall')},
});
