/* eslint-disable no-void -- React Native event callbacks intentionally start async host operations. */
import React, {useCallback, useEffect, useMemo, useState} from 'react';
import {
  ActivityIndicator,
  Image,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import type {
  CompatibilityEntry,
  EmulatorSettings,
  GameInstall,
  GameStatus,
  LauncherSettings,
  LibrarySortField,
  PatchSelection,
  ProcessSession,
} from './src/core/models';
import {DEFAULT_LAUNCHER_SETTINGS, DEFAULT_PROCESS_SESSION} from './src/core/models';
import {launchGame} from './src/core/launcher';
import {scanGameDirectories} from './src/core/scanner';
import {
  loadSettings,
  saveSettings,
  setCompatibility,
  setPatchSelection,
  setPerGameSettings,
} from './src/core/settings';
import {filterAndSortGames, fileImageUri} from './src/core/library';
import {windowsPathKey} from './src/core/paths';
import {
  GAME_STATUSES,
  gameStatusLabel,
  mergeCompatibilityEntries,
  refreshCompatibilityDatabase,
} from './src/core/compatibility';
import {applyPatchSelections, loadPatchPlan} from './src/core/patches';
import {
  confirmedSaveDataRemoval,
  existingSaveDataPaths,
  hostActionAvailability,
} from './src/core/actions';
import {
  applyProcessEvent,
  beginSession,
  failedSession,
  launchedSession,
  subscribeToProcessLifecycle,
} from './src/core/process';
import {parseTrophyPackage, type TrophySet} from './src/core/trophies';
import {BigPictureShell, type FirmwareShellIconPaths, type FirmwareShellMediaPaths} from './src/bigPicture/BigPictureShell';
import {ShellFocusNoiseProvider} from './src/bigPicture/ShellFocusNoise';
import {
  hasNativeProsperismoHost,
  getStartupRoute,
  prosperismoHost,
  resolveShellAssets,
  setBigPictureMode,
} from './src/native/ProsperismoHost';

const brandArtwork = {
  desktopDark: require('../../assets/branding/ps-iOS-ClearDark-1024.png'),
  desktopLight: require('../../assets/branding/ps-iOS-ClearLight-1024.png'),
};

type Route = 'desktop' | 'big-picture';
type Inspector = 'game' | 'settings' | 'patches' | 'trophies';

function Button({label, onPress, primary = false, disabled = false}: {
  label: string;
  onPress: () => void;
  primary?: boolean;
  disabled?: boolean;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{disabled}}
      disabled={disabled}
      onPress={onPress}
      style={({pressed}) => [styles.button, primary && styles.primaryButton, disabled && styles.disabled, pressed && styles.pressed]}>
      <Text style={[styles.buttonText, primary && styles.primaryButtonText]}>{label}</Text>
    </Pressable>
  );
}

function Toggle({label, value, onChange}: {label: string; value: boolean; onChange: (value: boolean) => void}) {
  return (
    <Pressable onPress={() => onChange(!value)} style={styles.settingRow} accessibilityRole="checkbox" accessibilityState={{checked: value}}>
      <Text style={styles.settingLabel}>{label}</Text>
      <Text style={[styles.toggle, value && styles.toggleOn]}>{value ? 'On' : 'Off'}</Text>
    </Pressable>
  );
}

function Choice<T extends string>({label, value, values, onChange}: {
  label: string;
  value: T;
  values: readonly T[];
  onChange: (value: T) => void;
}) {
  const next = () => onChange(values[(values.indexOf(value) + 1) % values.length]);
  return (
    <Pressable onPress={next} style={styles.settingRow}>
      <Text style={styles.settingLabel}>{label}</Text>
      <Text style={styles.choice}>{value}</Text>
    </Pressable>
  );
}

function SettingsEditor({value, onChange}: {value: EmulatorSettings; onChange: (value: EmulatorSettings) => void}) {
  const patch = <K extends keyof EmulatorSettings>(key: K, next: EmulatorSettings[K]) =>
    onChange({...value, [key]: next});
  return (
    <ScrollView style={styles.inspectorScroll}>
      <Text style={styles.fieldHelp}>Click a value to cycle the choices. Paths and refresh rate are directly editable.</Text>
      <Choice label="Resolution" value={value.screenResolution} values={['1280x720', '1920x1080']} onChange={next => patch('screenResolution', next)} />
      <View style={styles.settingRow}>
        <Text style={styles.settingLabel}>Vblank frequency</Text>
        <TextInput keyboardType="numeric" value={String(value.vblankFrequency)} onChangeText={text => patch('vblankFrequency', Number.parseInt(text, 10) || 60)} style={styles.inlineInput} />
      </View>
      <Toggle label="Vulkan validation" value={value.vulkanValidation} onChange={next => patch('vulkanValidation', next)} />
      <Toggle label="Shader validation" value={value.shaderValidation} onChange={next => patch('shaderValidation', next)} />
      <Choice label="Shader optimization" value={value.shaderOptimization} values={['None', 'Size', 'Performance']} onChange={next => patch('shaderOptimization', next)} />
      <Choice label="Shader log" value={value.shaderLogDirection} values={['Silent', 'Console', 'File']} onChange={next => patch('shaderLogDirection', next)} />
      <PathSetting label="Shader log folder" value={value.shaderLogFolder} onChange={next => patch('shaderLogFolder', next)} />
      <Toggle label="Command buffer dump" value={value.commandBufferDump} onChange={next => patch('commandBufferDump', next)} />
      <PathSetting label="Buffer dump folder" value={value.commandBufferDumpFolder} onChange={next => patch('commandBufferDumpFolder', next)} />
      <Choice label="Printf output" value={value.printfDirection} values={['Silent', 'Console', 'File']} onChange={next => patch('printfDirection', next)} />
      <PathSetting label="Printf file" value={value.printfOutputFile} onChange={next => patch('printfOutputFile', next)} />
      <Choice label="Profiler" value={value.profilerDirection} values={['None', 'Network']} onChange={next => patch('profilerDirection', next)} />
      <Toggle label="RenderDoc" value={value.renderDoc} onChange={next => patch('renderDoc', next)} />
      <Toggle label="NGG rectangle-list draw" value={value.nggRectlistDraw} onChange={next => patch('nggRectlistDraw', next)} />
    </ScrollView>
  );
}

function PathSetting({label, value, onChange}: {label: string; value: string; onChange: (value: string) => void}) {
  return (
    <View style={styles.settingRow}>
      <Text style={styles.settingLabel}>{label}</Text>
      <TextInput value={value} onChangeText={onChange} style={styles.pathSettingInput} />
    </View>
  );
}

function CompatibilityEditor({entry, onChange, disabled}: {
  entry: CompatibilityEntry;
  onChange: (entry: CompatibilityEntry) => void;
  disabled: boolean;
}) {
  const index = GAME_STATUSES.indexOf(entry.status);
  return (
    <View>
      <Text style={styles.sectionHeading}>Compatibility</Text>
      {disabled && <Text style={styles.fieldHelp}>A title ID is required before compatibility can be recorded.</Text>}
      <Button disabled={disabled} label={`Status: ${gameStatusLabel(entry.status)}`} onPress={() => onChange({...entry, status: GAME_STATUSES[(index + 1) % GAME_STATUSES.length]})} />
      <TextInput
        editable={!disabled}
        multiline
        placeholder="Compatibility notes"
        placeholderTextColor="#647087"
        value={entry.comment}
        onChangeText={comment => onChange({...entry, comment})}
        style={styles.commentInput}
      />
    </View>
  );
}

function DesktopLauncher({
  games, settings, session, busy, error, onChooseFolders, onRefresh, onLaunch,
  onSaveSettings, onBigPicture, onError,
  onClearUntrackedSession,
  onSaveRoots,
  onRefreshCompatibility,
}: {
  games: GameInstall[];
  settings: LauncherSettings;
  session: ProcessSession;
  busy: boolean;
  error?: string;
  onChooseFolders: () => void;
  onRefresh: () => void;
  onLaunch: (game: GameInstall) => void;
  onSaveSettings: (settings: LauncherSettings) => Promise<void>;
  onBigPicture: () => void;
  onError: (message?: string) => void;
  onClearUntrackedSession: () => void;
  onSaveRoots: (roots: string[]) => Promise<void>;
  onRefreshCompatibility: () => Promise<void>;
}) {
  const [selectedPath, setSelectedPath] = useState<string>();
  const [query, setQuery] = useState('');
  const [inspector, setInspector] = useState<Inspector>('game');
  const [settingsScope, setSettingsScope] = useState<'global' | 'game'>('global');
  const [patches, setPatches] = useState<PatchSelection[]>([]);
  const [patchSource, setPatchSource] = useState<{path: string; source: string}>();
  const [trophyFiles, setTrophyFiles] = useState<string[]>([]);
  const [trophySets, setTrophySets] = useState<TrophySet[]>([]);
  const [trophyError, setTrophyError] = useState<string>();
  const [pendingSavePaths, setPendingSavePaths] = useState<string[]>([]);
  const capabilities = hostActionAvailability(prosperismoHost);
  const library = useMemo(() => filterAndSortGames(
    games, settings.compatibility, query, settings.library.sortField, settings.library.sortDirection,
  ), [games, query, settings.compatibility, settings.library]);
  const selected = games.find(game => game.gamePath === selectedPath) ?? library[0];
  const compatibility = selected?.titleId
    ? settings.compatibility[selected.titleId.trim().toUpperCase()] ?? {status: 'Unknown' as GameStatus, comment: ''}
    : {status: 'Unknown' as GameStatus, comment: ''};
  const selectedCustomSettings = selected ? settings.perGame[windowsPathKey(selected.gamePath)] : undefined;

  useEffect(() => {
    setPatchSource(undefined);
    setPatches([]);
    setTrophyFiles([]);
    setTrophySets([]);
    setTrophyError(undefined);
    setPendingSavePaths([]);
    if (!selected) {
      return;
    }
    prosperismoHost.findEmulator().then(executable =>
      loadPatchPlan(prosperismoHost, executable, selected.titleId, settings),
    ).then(plan => {
      if (plan) {
        setPatchSource({path: plan.path, source: plan.source});
        setPatches(plan.patches);
      }
    }).catch(() => undefined);
    prosperismoHost.listDirectory(`${selected.baseDirectory}\\sce_sys\\trophy2`).then(async entries => {
      const files = entries.filter(entry => entry.kind === 'file' && /^trophy\d*\.ucp$/i.test(entry.name)).map(entry => entry.path);
      setTrophyFiles(files);
      if (prosperismoHost.readBinaryFile) {
        const sets = await Promise.all(files.map(async file =>
          parseTrophyPackage(await prosperismoHost.readBinaryFile!(file), file),
        ));
        setTrophySets(sets);
      }
    }).catch(reason => setTrophyError(reason instanceof Error ? reason.message : String(reason)));
  }, [selected, settings]);

  const persist = (next: LauncherSettings) => onSaveSettings(next).catch(reason =>
    onError(reason instanceof Error ? reason.message : String(reason)));
  const cycleSort = () => {
    const fields: LibrarySortField[] = ['titleName', 'titleId', 'gameVersion', 'firmwareVersion', 'gamePath', 'status', 'comment'];
    const current = fields.indexOf(settings.library.sortField);
    void persist({...settings, library: {...settings.library, sortField: fields[(current + 1) % fields.length]}});
  };
  const updatePatch = async (patch: PatchSelection) => {
    if (!selected || !patchSource || !prosperismoHost.writeTextFile) {
      return;
    }
    const nextPatches = patches.map(item => item.name === patch.name ? patch : item);
    await prosperismoHost.writeTextFile(patchSource.path, applyPatchSelections(patchSource.source, nextPatches));
    setPatches(nextPatches);
    await persist(setPatchSelection(settings, selected.titleId, patch.name, patch.enabled));
  };
  const prepareSaveRemoval = async () => {
    if (!selected) {
      return;
    }
    const emulator = await prosperismoHost.findEmulator();
    setPendingSavePaths(await existingSaveDataPaths(prosperismoHost, emulator, selected.titleId));
  };
  const removeSaveData = async () => {
    if (!selected || !prosperismoHost.removeDirectories) {
      return;
    }
    const failed = await prosperismoHost.removeDirectories(confirmedSaveDataRemoval(selected.titleId, pendingSavePaths));
    setPendingSavePaths([]);
    if (failed.length) {
      onError(`Could not remove:\n${failed.join('\n')}`);
    }
  };

  return (
    <View style={styles.desktopRoot}>
      <View style={styles.desktopHeader}>
        <View style={styles.brandRow}><Image source={brandArtwork.desktopDark} style={styles.desktopBrandIcon} /><View><Text style={styles.wordmark}>Prosperismo</Text><Text style={styles.subtitle}>Desktop launcher</Text></View></View>
        <View style={styles.actionRow}><Button label="Add folders" onPress={onChooseFolders} /><Button label="Refresh" onPress={onRefresh} /><Button label="Big Picture" onPress={onBigPicture} primary /></View>
      </View>
      {!hasNativeProsperismoHost && <Text style={styles.warning}>Windows host adapter unavailable. Library actions are explicitly disabled; metadata and settings remain inspectable.</Text>}
      {error && <Text style={styles.error}>{error}</Text>}
      <View style={styles.toolbar}>
        <TextInput value={query} onChangeText={setQuery} placeholder="Search name or serial" placeholderTextColor="#647087" style={styles.searchInput} />
        <Button label={`Sort: ${settings.library.sortField}`} onPress={cycleSort} />
        <Button label={settings.library.sortDirection === 'ascending' ? 'Ascending' : 'Descending'} onPress={() => void persist({...settings, library: {...settings.library, sortDirection: settings.library.sortDirection === 'ascending' ? 'descending' : 'ascending'}})} />
        <Text style={styles.sessionText}>{session.phase === 'idle' ? `${library.length} games` : `${session.titleName ?? 'Game'}: ${session.phase}${session.tracking === 'launch-only' ? ' (exit untracked)' : ''}`}</Text>
        {session.phase === 'running' && session.tracking === 'launch-only' && <Button label="Process has exited" onPress={onClearUntrackedSession} />}
      </View>
      <View style={styles.desktopBody}>
        <View style={styles.libraryPanel}>
          <View style={styles.tableHeader}><Text style={[styles.cell, styles.nameCell]}>Name</Text><Text style={styles.cell}>Serial</Text><Text style={styles.cell}>Version</Text><Text style={styles.cell}>Firmware</Text><Text style={styles.cell}>Status</Text></View>
          <ScrollView style={styles.gameTable}>
            {library.map(game => (
              <Pressable key={game.gamePath} onPress={() => setSelectedPath(game.gamePath)} onLongPress={() => onLaunch(game)} style={[styles.tableRow, selected?.gamePath === game.gamePath && styles.selectedRow]}>
                <Text numberOfLines={1} style={[styles.cell, styles.nameCell, styles.rowText]}>{game.titleName}</Text><Text style={[styles.cell, styles.rowText]}>{game.titleId || '—'}</Text><Text style={[styles.cell, styles.rowText]}>{game.gameVersion || '—'}</Text><Text style={[styles.cell, styles.rowText]}>{game.firmwareVersion || '—'}</Text><Text style={[styles.cell, styles.rowText]}>{gameStatusLabel(game.compatibility.status)}</Text>
              </Pressable>
            ))}
            {!busy && library.length === 0 && <Text style={styles.empty}>{games.length ? 'No games match the search.' : 'Add one or more game folders to scan recursively for eboot.bin.'}</Text>}
          </ScrollView>
          {selected && <View style={styles.selectionBar}><View style={styles.selectionCopy}><Text style={styles.selectionTitle}>{selected.titleName}</Text><Text numberOfLines={1} style={styles.pathText}>{selected.baseDirectory}</Text></View><Button disabled={session.phase === 'launching' || session.phase === 'running'} label="Run" onPress={() => onLaunch(selected)} primary /></View>}
        </View>
        <View style={styles.settingsPanel}>
          <Image source={brandArtwork.desktopLight} style={styles.desktopPanelWatermark} />
          <View style={styles.tabs}>{(['game', 'settings', 'patches', 'trophies'] as Inspector[]).map(value => <Button key={value} label={value[0].toUpperCase() + value.slice(1)} primary={inspector === value} onPress={() => setInspector(value)} />)}</View>
          {inspector === 'game' && selected && <ScrollView style={styles.inspectorScroll}>
            {(selected.backgroundPath ?? selected.artworkPath) && <Image source={{uri: fileImageUri(selected.backgroundPath ?? selected.artworkPath)}} resizeMode="cover" style={styles.pic0} />}
            <Text style={styles.sectionHeading}>{selected.titleName}</Text><Text style={styles.pathText}>{selected.titleId || 'No title ID'} · {selected.gameVersion || 'Unknown version'} · firmware {selected.firmwareVersion || 'unknown'}</Text>
            <CompatibilityEditor disabled={!selected.titleId} entry={compatibility} onChange={entry => void persist(setCompatibility(settings, selected.titleId, entry))} />
            <Button label="Refresh compatibility database" onPress={() => void onRefreshCompatibility()} />
            <View style={styles.stack}><Button disabled={!capabilities.openGameFolder} label={capabilities.openGameFolder ? 'Open game folder' : 'Open folder (host unavailable)'} onPress={() => void prosperismoHost.openPath?.(selected.baseDirectory)} />
              <Button disabled={!capabilities.removeSaveData || !selected.titleId || session.gamePath === selected.gamePath && session.phase === 'running'} label="Find save data…" onPress={() => void prepareSaveRemoval()} />
              {pendingSavePaths.length > 0 && <View style={styles.dangerBox}><Text style={styles.dangerText}>This permanently deletes:{'\n'}{pendingSavePaths.join('\n')}</Text><Button label={`Confirm delete ${pendingSavePaths.length} folder(s)`} onPress={() => void removeSaveData()} /></View>}
            </View>
          </ScrollView>}
          {inspector === 'game' && !selected && <Text style={styles.empty}>Select a game.</Text>}
          {inspector === 'settings' && <View style={styles.flexOne}>
            <View style={styles.settingsScope}>
              <View style={styles.actionRow}><Button label="Global" primary={settingsScope === 'global'} onPress={() => setSettingsScope('global')} /><Button label="Selected game" disabled={!selected} primary={settingsScope === 'game'} onPress={() => setSettingsScope('game')} /></View>
              <Text style={styles.sectionHeading}>{settingsScope === 'game' && selected ? `${selected.titleName}: ${selectedCustomSettings ? 'custom settings' : 'inherits global settings'}` : 'Global settings'}</Text>
              {settingsScope === 'game' && selected && <View style={styles.actionRow}><Button label="Use custom copy" disabled={Boolean(selectedCustomSettings)} onPress={() => void persist(setPerGameSettings(settings, selected.gamePath, {...settings.global}))} /><Button label="Clear custom" disabled={!selectedCustomSettings} onPress={() => void persist(setPerGameSettings(settings, selected.gamePath, undefined))} /></View>}
            </View>
            {settingsScope === 'global' && <SettingsEditor value={settings.global} onChange={value => void persist({...settings, global: value})} />}
            {settingsScope === 'game' && selected && selectedCustomSettings && <SettingsEditor value={selectedCustomSettings} onChange={value => void persist(setPerGameSettings(settings, selected.gamePath, value))} />}
            {settingsScope === 'game' && selected && !selectedCustomSettings && <Text style={styles.fieldHelp}>This title currently uses the global settings. Choose “Use custom copy” before editing.</Text>}
            {settingsScope === 'global' && <View style={styles.rootList}><Text style={styles.sectionHeading}>Game folders</Text>{settings.gameDirectories.map(root => <View key={root} style={styles.rootRow}><Text numberOfLines={1} style={styles.pathText}>{root}</Text><Button label="Remove" onPress={() => void onSaveRoots(settings.gameDirectories.filter(item => item !== root))} /></View>)}</View>}
          </View>}
          {inspector === 'patches' && <ScrollView style={styles.inspectorScroll}><Text style={styles.sectionHeading}>Patches (experimental)</Text>{!selected?.titleId.startsWith('PPSA') && <Text style={styles.fieldHelp}>Qt parity: patches are exposed only for PPSA title IDs.</Text>}{selected?.titleId.startsWith('PPSA') && !patchSource && <Text style={styles.fieldHelp}>No local patch plan was found beside the emulator.</Text>}{patches.map(patch => <Pressable disabled={!capabilities.writePatchPlan} key={patch.name} onPress={() => void updatePatch({...patch, enabled: !patch.enabled})} style={styles.patchRow}><Text style={styles.settingLabel}>{patch.name}</Text><Text style={styles.choice}>{patch.enabled ? 'Enabled' : 'Disabled'}</Text></Pressable>)}{patchSource && !capabilities.writePatchPlan && <Text style={styles.warning}>Patch plan is readable, but changes are disabled until the host provides atomic text writes.</Text>}</ScrollView>}
          {inspector === 'trophies' && <ScrollView style={styles.inspectorScroll}><Text style={styles.sectionHeading}>Trophies</Text>{trophyFiles.length === 0 && <Text style={styles.fieldHelp}>No Trophy*.ucp package found in sce_sys/trophy2.</Text>}{trophySets.map(set => <View key={set.title}><Text style={styles.sectionHeading}>{set.title}</Text>{set.trophies.map(trophy => <View key={trophy.id} style={styles.trophyRow}><Text style={styles.trophyName}>{trophy.name}</Text><Text style={styles.trophyGrade}>{trophy.grade}</Text><Text style={styles.fieldHelp}>{trophy.detail}{trophy.hasReward && trophy.reward ? `\nReward: ${trophy.reward}` : ''}</Text></View>)}</View>)}{trophyFiles.length > 0 && !capabilities.readTrophies && <Text style={styles.warning}>Packages were found. The safe UCP parser is ready, but viewing contents requires the binary-read host capability.</Text>}{trophyError && <Text style={styles.error}>{trophyError}</Text>}</ScrollView>}
        </View>
      </View>
      {busy && <View style={styles.busy}><ActivityIndicator color="#6da8ff" /><Text style={styles.muted}>Scanning…</Text></View>}
    </View>
  );
}

export default function App() {
  const [route, setRoute] = useState<Route>('desktop');
  const [settings, setSettings] = useState<LauncherSettings>(DEFAULT_LAUNCHER_SETTINGS);
  const [games, setGames] = useState<GameInstall[]>([]);
  const [session, setSession] = useState<ProcessSession>(DEFAULT_PROCESS_SESSION);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [firmwareShellIcons, setFirmwareShellIcons] = useState<FirmwareShellIconPaths>({});
  const [firmwareShellMedia, setFirmwareShellMedia] = useState<FirmwareShellMediaPaths>({});
  const [playColdBoot, setPlayColdBoot] = useState(true);
  const refresh = useCallback(async (current: LauncherSettings) => {
    setBusy(true); setError(undefined);
    try { setGames(await scanGameDirectories(prosperismoHost, current.gameDirectories, current.global, current.perGame)); }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
    finally { setBusy(false); }
  }, []);
  useEffect(() => { loadSettings(prosperismoHost).then(value => { setSettings(value); return refresh(value); }).catch(reason => setError(reason instanceof Error ? reason.message : String(reason))); }, [refresh]);
  useEffect(() => {
    let mounted = true;
    getStartupRoute()
      .then(startupRoute => {
        if (mounted && startupRoute === 'big-picture') {
          return setBigPictureMode(true).then(() => {
            if (mounted) {
              setRoute('big-picture');
            }
          });
        }
      })
      .catch(reason => {
        if (mounted) {
          setError(reason instanceof Error ? reason.message : String(reason));
        }
      });
    return () => {
      mounted = false;
    };
  }, []);
  useEffect(() => subscribeToProcessLifecycle(prosperismoHost, event => setSession(current => applyProcessEvent(current, event))), []);
  useEffect(() => {
    let mounted = true;
    resolveShellAssets()
      .then(paths => {
        if (mounted && paths) {
          // Matches SharpEmu's ShellIcons policy: only bitmap art is usable.
          // The shell keeps most of its pictogram set (search included) as
          // SVG, which Image cannot decode, so vector-only entries resolve to
          // undefined and the caller keeps its own glyph.
          const bitmapOnly = (path: string): string | undefined =>
            path && path.toLowerCase().endsWith('.png') ? path : undefined;
          setFirmwareShellIcons({
            settings: bitmapOnly(paths.settingsIcon),
            library: bitmapOnly(paths.libraryIcon),
            desktop: bitmapOnly(paths.desktopIcon),
            search: bitmapOnly(paths.searchIcon),
            genericGame: bitmapOnly(paths.genericGameIcon),
            focusNoise: bitmapOnly(paths.focusNoise),
          });
          setFirmwareShellMedia({
            nativeSequenceDirectory: paths.nativeSequenceDirectory || undefined,
            coldBootChime: paths.coldBootChime || undefined,
            homeBgm: paths.homeBgm || undefined,
          });
        }
      })
      .catch(() => undefined);
    return () => { mounted = false; };
  }, []);
  const persist = useCallback(async (next: LauncherSettings) => { setSettings(next); await saveSettings(prosperismoHost, next); await refresh(next); }, [refresh]);
  const updateRoots = useCallback(async (roots: string[]) => persist({...settings, gameDirectories: [...new Set(roots)]}), [persist, settings]);
  const chooseFolders = useCallback(async () => { try { const roots = await prosperismoHost.chooseGameDirectories(); if (roots.length) { await updateRoots([...settings.gameDirectories, ...roots]); } } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); } }, [settings.gameDirectories, updateRoots]);
  const run = useCallback(async (game: GameInstall) => {
    const pending = beginSession(game, Boolean(prosperismoHost.subscribeProcessEvents));
    setSession(pending); setError(undefined);
    try { await launchGame(prosperismoHost, game, settings); setSession(current => launchedSession(current)); }
    catch (reason) { setSession(current => failedSession(current, reason)); setError(reason instanceof Error ? reason.message : String(reason)); }
  }, [settings]);
  const refreshCompatibility = useCallback(async () => {
    setError(undefined);
    try {
      const remote = await refreshCompatibilityDatabase(fetch);
      await persist({...settings, compatibility: mergeCompatibilityEntries(remote, settings.compatibility)});
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    }
  }, [persist, settings]);
  const switchRoute = useCallback((next: Route) => {
    void setBigPictureMode(next === 'big-picture')
      .catch(reason => setError(reason instanceof Error ? reason.message : String(reason)))
      .finally(() => setRoute(next));
  }, []);
  const completeColdBoot = useCallback(() => setPlayColdBoot(false), []);
  return route === 'desktop'
    ? <DesktopLauncher games={games} settings={settings} session={session} busy={busy} error={error} onChooseFolders={chooseFolders} onRefresh={() => refresh(settings)} onRefreshCompatibility={refreshCompatibility} onLaunch={run} onSaveSettings={persist} onSaveRoots={updateRoots} onBigPicture={() => switchRoute('big-picture')} onError={setError} onClearUntrackedSession={() => setSession(DEFAULT_PROCESS_SESSION)} />
    : <ShellFocusNoiseProvider path={firmwareShellIcons.focusNoise}>
        <BigPictureShell
          firmwareShellIcons={firmwareShellIcons}
          firmwareShellMedia={firmwareShellMedia}
          playColdBoot={playColdBoot}
          onColdBootComplete={completeColdBoot}
          emulatorRunning={session.phase === 'launching' || session.phase === 'running'}
          games={games}
          settings={settings}
          onAddFolders={() => { void chooseFolders(); }}
          onSaveSettings={next => { void persist(next); }}
          onDesktop={() => switchRoute('desktop')}
          errorMessage={error}
          onDismissError={() => setError(undefined)}
          onLaunch={game => {
            if (session.phase !== 'launching' && session.phase !== 'running') {
              void run(game);
            }
          }}
        />
      </ShellFocusNoiseProvider>;
}

const styles = StyleSheet.create({
  flexOne: {flex: 1}, desktopRoot: {flex: 1, backgroundColor: '#0b0f16', padding: 22}, desktopHeader: {flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', marginBottom: 14}, brandRow: {flexDirection: 'row', alignItems: 'center', gap: 12}, desktopBrandIcon: {width: 48, height: 48, borderRadius: 10}, desktopPanelWatermark: {position: 'absolute', right: -30, bottom: -30, width: 180, height: 180, opacity: 0.055}, wordmark: {fontSize: 30, fontWeight: '700', color: '#f5f8ff'}, subtitle: {fontSize: 13, color: '#8491a8'}, actionRow: {flexDirection: 'row', gap: 8, flexWrap: 'wrap'}, button: {minHeight: 36, justifyContent: 'center', paddingHorizontal: 13, borderRadius: 7, borderWidth: 1, borderColor: '#354054', backgroundColor: '#151c28'}, primaryButton: {backgroundColor: '#f4f7ff', borderColor: '#fff'}, buttonText: {color: '#dce5f5', fontWeight: '600', fontSize: 12}, primaryButtonText: {color: '#151a23'}, pressed: {opacity: 0.72}, disabled: {opacity: 0.38}, warning: {color: '#e8c46c', backgroundColor: '#2a2212', borderWidth: 1, borderColor: '#5e4d23', padding: 9, marginVertical: 7, borderRadius: 6}, error: {color: '#ffb8b8', backgroundColor: '#351819', padding: 9, marginBottom: 8, borderRadius: 6}, toolbar: {flexDirection: 'row', gap: 8, marginBottom: 10, alignItems: 'center'}, searchInput: {flex: 1, height: 38, color: '#e4ebf8', backgroundColor: '#101722', borderWidth: 1, borderColor: '#303b50', borderRadius: 6, paddingHorizontal: 10}, sessionText: {color: '#8290a8', marginLeft: 8}, desktopBody: {flex: 1, flexDirection: 'row', gap: 14}, libraryPanel: {flex: 1, borderWidth: 1, borderColor: '#283144', backgroundColor: '#101722', borderRadius: 9, overflow: 'hidden'}, settingsPanel: {width: 390, borderWidth: 1, borderColor: '#283144', backgroundColor: '#101722', borderRadius: 9, padding: 13, overflow: 'hidden'}, tabs: {gap: 5, paddingBottom: 10}, tableHeader: {flexDirection: 'row', backgroundColor: '#181f2b', borderBottomWidth: 1, borderColor: '#293246', paddingVertical: 9, paddingHorizontal: 11}, tableRow: {flexDirection: 'row', paddingVertical: 11, paddingHorizontal: 11, borderBottomWidth: 1, borderColor: '#202a3a'}, selectedRow: {backgroundColor: '#183e69'}, cell: {width: 105, color: '#9ca9bd', fontSize: 12}, nameCell: {flex: 1, width: undefined}, rowText: {fontSize: 13, color: '#dbe4f3'}, gameTable: {flex: 1}, empty: {color: '#7e8ba1', padding: 24, textAlign: 'center'}, selectionBar: {flexDirection: 'row', alignItems: 'center', padding: 13, borderTopWidth: 1, borderColor: '#293246'}, selectionCopy: {flex: 1}, selectionTitle: {color: '#f2f6ff', fontWeight: '700'}, pathText: {color: '#7f8ca2', fontSize: 11, marginVertical: 3}, muted: {color: '#8290a8'}, busy: {position: 'absolute', bottom: 20, right: 410, flexDirection: 'row', gap: 8}, inspectorScroll: {flex: 1}, sectionHeading: {fontSize: 16, fontWeight: '700', color: '#eef4ff', marginVertical: 10}, fieldHelp: {color: '#7f8ca2', fontSize: 12, marginVertical: 8, lineHeight: 17}, settingRow: {minHeight: 43, flexDirection: 'row', alignItems: 'center', borderBottomWidth: 1, borderColor: '#222c3b', gap: 8}, settingLabel: {flex: 1, color: '#d8e1ef', fontSize: 12}, toggle: {color: '#8694a9', padding: 6}, toggleOn: {color: '#72c5ff'}, choice: {color: '#8ecaff', fontSize: 12}, inlineInput: {width: 65, color: '#e4ebf8', borderWidth: 1, borderColor: '#354054', padding: 5, textAlign: 'right'}, pathSettingInput: {width: 160, color: '#e4ebf8', borderWidth: 1, borderColor: '#354054', padding: 5}, commentInput: {height: 72, color: '#e4ebf8', borderWidth: 1, borderColor: '#354054', padding: 8, textAlignVertical: 'top', marginVertical: 8}, stack: {gap: 7, marginVertical: 10}, dangerBox: {borderWidth: 1, borderColor: '#8a3d3d', padding: 8}, dangerText: {color: '#ffb8b8', fontSize: 11, marginBottom: 8}, patchRow: {minHeight: 48, flexDirection: 'row', alignItems: 'center', borderBottomWidth: 1, borderColor: '#283144'}, trophyFile: {color: '#dbe4f3', paddingVertical: 8}, trophyRow: {borderBottomWidth: 1, borderColor: '#283144', paddingVertical: 8}, trophyName: {color: '#eef4ff', fontWeight: '700'}, trophyGrade: {color: '#8ecaff', fontSize: 11, marginTop: 2}, settingsScope: {borderBottomWidth: 1, borderColor: '#283144', paddingBottom: 8}, rootList: {borderTopWidth: 1, borderColor: '#283144', marginTop: 8, paddingTop: 4}, rootRow: {flexDirection: 'row', alignItems: 'center', gap: 6}, pic0: {width: '100%', height: 140, borderRadius: 7, backgroundColor: '#08101a'}, shellRoot: {flex: 1, backgroundColor: '#071424', paddingHorizontal: 62, paddingVertical: 34, overflow: 'hidden'}, glowTop: {position: 'absolute', top: -260, left: 180, width: 900, height: 600, borderRadius: 450, backgroundColor: '#183b61', opacity: 0.62}, glowSide: {position: 'absolute', right: -250, bottom: -260, width: 720, height: 720, borderRadius: 360, backgroundColor: '#102c4c', opacity: 0.65}, shellWatermark: {position: 'absolute', right: 65, top: 125, width: 430, height: 430, opacity: 0.16}, shellTopBar: {zIndex: 1, flexDirection: 'row', justifyContent: 'space-between'}, shellBrand: {color: '#f5f8ff', fontSize: 27}, navBand: {flexDirection: 'row', gap: 34}, navItem: {color: '#93a2b5', fontSize: 22}, navItemActive: {color: '#fff', fontWeight: '700'}, systemIcons: {flexDirection: 'row', gap: 16}, systemButton: {width: 56, height: 56, borderRadius: 28, alignItems: 'center', justifyContent: 'center', backgroundColor: '#ffffff18'}, systemGlyph: {fontSize: 25, color: '#fff'}, hero: {zIndex: 1, marginTop: 70, width: 650, alignItems: 'flex-start'}, heroArtwork: {position: 'absolute', width: 650, height: 270, opacity: 0.18, borderRadius: 15}, heroEyebrow: {fontSize: 13, letterSpacing: 4, color: '#88b9e2'}, heroTitle: {fontSize: 48, lineHeight: 56, color: '#fff', fontWeight: '300', marginVertical: 10}, heroMeta: {fontSize: 15, color: '#a9bacd', marginBottom: 24}, tileRail: {zIndex: 1, gap: 18, marginTop: 55}, gameTile: {width: 146, padding: 6, borderRadius: 14}, gameTileFocused: {backgroundColor: '#fff', transform: [{scale: 1.08}]}, tileArtwork: {height: 132, width: 134, borderRadius: 9, alignItems: 'center', justifyContent: 'center', backgroundColor: '#244f77'}, tileMonogram: {color: '#dceeff', fontSize: 50}, tileTitle: {color: '#edf5ff', marginTop: 10, fontSize: 13}, tileTitleFocused: {color: '#152235'}, allGamesTile: {width: 146, padding: 6, alignItems: 'center'}, allGamesGlyph: {height: 132, width: 132, borderRadius: 66, backgroundColor: '#ffffff14', color: '#fff', fontSize: 46, textAlign: 'center', textAlignVertical: 'center'}, shellHint: {position: 'absolute', right: 50, bottom: 28, color: '#7890aa', fontSize: 12}, shellSettings: {zIndex: 1, marginTop: 70, width: 670}, settingsCategory: {height: 62, flexDirection: 'row', alignItems: 'center', borderRadius: 7, paddingHorizontal: 13, marginBottom: 5}, settingsCategoryFocused: {backgroundColor: '#f8fbff'}, settingsGlyph: {width: 34, height: 34, borderRadius: 17, backgroundColor: '#3f678e', marginRight: 18}, settingsCategoryText: {flex: 1, color: '#b8c8d9', fontSize: 20}, chevron: {color: '#647e98', fontSize: 30},
});
