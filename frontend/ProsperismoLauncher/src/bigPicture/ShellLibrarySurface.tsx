import React, {useEffect, useMemo, useRef, useState} from 'react';
import {
  Image,
  Pressable,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import type {GameInstall, LibrarySortField, SortDirection} from '../core/models';
import {shellTextStyle} from './shellTypography';
import {ShellButtonPrompts} from './ShellUtilitySurfaces';
import {ShellFocusOverlay} from './ShellFocusOverlay';
import {
  SHELL_LIBRARY_METRICS,
  SHELL_LIBRARY_SORT_OPTIONS,
  shellLibraryColumnLeft,
  shellLibraryMoveIndex,
  shellLibraryRowTop,
  shellLibraryScrollFor,
  sortShellGames,
} from './shellSurfaces';

function localImageUri(path?: string): string | undefined {
  if (!path) {
    return undefined;
  }
  if (/^[a-z]+:\/\//i.test(path)) {
    return path;
  }
  return `file:///${path.replace(/\\/g, '/')}`;
}

function FallbackArt() {
  return <View style={styles.fallbackArt}>
    <View style={styles.fallbackMark} />
  </View>;
}

function LibraryTile({game, focused, onFocus, onPress}: {
  game: GameInstall;
  focused: boolean;
  onFocus(): void;
  onPress(): void;
}) {
  const art = localImageUri(game.iconPath ?? game.artworkPath);
  return <Pressable
    accessibilityLabel={`${game.titleName}. ${game.titleId || 'Local game'}`}
    accessibilityRole="button"
    onFocus={onFocus}
    onHoverIn={onFocus}
    onPress={onPress}
    style={styles.tile}>
    {art ? <Image resizeMode="cover" source={{uri: art}} style={styles.tileArt} /> : <FallbackArt />}
    <View pointerEvents="none" style={[styles.tileGradient, focused ? styles.tileGradientFocused : styles.tileGradientRest]} />
    {focused && <View pointerEvents="none" style={styles.tileMeta}>
      <Text numberOfLines={1} style={styles.tileTitle}>{game.titleName}</Text>
      <Text numberOfLines={1} style={styles.tileSubLabel}>{game.titleId || 'Local game'}</Text>
    </View>}
    <ShellFocusOverlay active={focused} width={296} height={296} radius={0} />
  </Pressable>;
}

export function ShellAllGamesSurface({games, initialField = 'titleName', initialDirection = 'ascending', onLaunch, onBack, onAddFolder, onOptions}: {
  games: readonly GameInstall[];
  initialField?: LibrarySortField;
  initialDirection?: SortDirection;
  onLaunch(game: GameInstall): void;
  onBack(): void;
  onAddFolder?(): void;
  onOptions?(game: GameInstall): void;
}) {
  const [field, setField] = useState<LibrarySortField>(initialField);
  const [direction, setDirection] = useState<SortDirection>(initialDirection);
  const [selectedIndex, setSelectedIndex] = useState(games.length ? 0 : -1);
  const [sortOpen, setSortOpen] = useState(false);
  const [sortIndex, setSortIndex] = useState(0);
  const tileRefs = useRef<any[]>([]);
  const sorted = useMemo(() => sortShellGames(games, field, direction), [direction, field, games]);
  const selected = selectedIndex >= 0 ? sorted[selectedIndex] : undefined;
  const viewportHeight = 1080 - 104;
  const scroll = shellLibraryScrollFor(selectedIndex, sorted.length, viewportHeight);
  const selectedSortLabel = SHELL_LIBRARY_SORT_OPTIONS.find(option => option.field === field && option.direction === direction)?.label
    ?? `${field} ${direction}`;

  useEffect(() => {
    setSelectedIndex(current => sorted.length === 0 ? -1 : Math.max(0, Math.min(sorted.length - 1, current)));
  }, [sorted.length]);

  const move = (directionName: 'left' | 'right' | 'up' | 'down') => {
    const next = shellLibraryMoveIndex(selectedIndex, sorted.length, directionName);
    setSelectedIndex(next);
    tileRefs.current[next]?.focus?.();
  };

  const onKeyDown = (event: any) => {
    const key = event?.nativeEvent?.key;
    if (sortOpen) {
      if (key === 'ArrowUp' || key === 'GamepadDPadUp') {
        setSortIndex(index => Math.max(0, index - 1));
      } else if (key === 'ArrowDown' || key === 'GamepadDPadDown') {
        setSortIndex(index => Math.min(SHELL_LIBRARY_SORT_OPTIONS.length - 1, index + 1));
      } else if (key === 'Enter' || key === 'GamepadA') {
        const option = SHELL_LIBRARY_SORT_OPTIONS[sortIndex];
        setField(option.field);
        setDirection(option.direction);
        setSortOpen(false);
      } else if (key === 'Escape' || key === 'GamepadB' || key === 'ArrowLeft') {
        setSortOpen(false);
      } else {
        return;
      }
      event.stopPropagation?.();
      return;
    }
    if (key === 'ArrowLeft' || key === 'GamepadDPadLeft') {
      move('left');
    } else if (key === 'ArrowRight' || key === 'GamepadDPadRight') {
      move('right');
    } else if (key === 'ArrowUp' || key === 'GamepadDPadUp') {
      move('up');
    } else if (key === 'ArrowDown' || key === 'GamepadDPadDown') {
      move('down');
    } else if (key === 'Enter' || key === 'GamepadA') {
      if (selected) {
        onLaunch(selected);
      }
    } else if (key === 'ContextMenu' || key === 'GamepadMenu' || key === 'F10') {
      if (selected) {
        onOptions?.(selected);
      }
    } else if (key === 'Escape' || key === 'GamepadB') {
      onBack();
    } else {
      return;
    }
    event.stopPropagation?.();
  };

  return <View accessibilityLabel="All Games" style={styles.surface} {...({onKeyDownCapture: onKeyDown} as any)}>
    <Text style={styles.pageTitle}>Game Library</Text>
    {sorted.length > 0 ? <>
      <Pressable accessibilityLabel="Sort games" onPress={() => setSortOpen(open => !open)} style={styles.sortButton}>
        <View style={styles.sortBar} /><View style={[styles.sortBar, styles.sortBarMiddle]} /><View style={[styles.sortBar, styles.sortBarShort]} />
      </Pressable>
      <View style={styles.viewport}>
        <View style={[styles.gridContent, {top: -scroll}]}>
          <Text style={styles.sectionTitle}>Console Storage ({sorted.length})</Text>
          <Text numberOfLines={1} style={styles.sortHeader}>{selectedSortLabel}</Text>
          {sorted.map((game, index) => <View key={game.gamePath} style={[styles.tileSlot, {left: shellLibraryColumnLeft(index), top: shellLibraryRowTop(index)}]}>
            <LibraryTile
              game={game}
              focused={index === selectedIndex}
              onFocus={() => setSelectedIndex(index)}
              onPress={() => onLaunch(game)}
            />
          </View>)}
        </View>
      </View>
      {sortOpen && <View style={styles.sortPanel}>
        {SHELL_LIBRARY_SORT_OPTIONS.map((option, index) => <Pressable
          key={option.label}
          onFocus={() => setSortIndex(index)}
          onPress={() => { setField(option.field); setDirection(option.direction); setSortOpen(false); }}
          style={styles.sortOption}>
          <ShellFocusOverlay active={index === sortIndex} width={384} height={72} radius={16} />
          <Text style={styles.sortCheck}>{option.field === field && option.direction === direction ? '\u2713' : ''}</Text>
          <Text style={styles.sortOptionText}>{option.label}</Text>
        </Pressable>)}
      </View>}
    </> : <View style={styles.emptyHost}>
      <Text style={styles.emptyHeading}>Nothing installed</Text>
      <Text style={styles.emptyCopy}>Installed games and apps appear here.</Text>
      <Pressable accessibilityRole="button" onPress={onAddFolder} style={styles.emptyButton}>
        <Text style={styles.emptyButtonText}>Add game folder</Text>
      </Pressable>
    </View>}
    <ShellButtonPrompts prompts={[
      {kind: 'confirm', label: 'Select'},
      ...(sorted.length > 0 ? [{kind: 'options' as const, label: 'Options'}] : []),
      {kind: 'back', label: 'Back'},
    ]} />
  </View>;
}

const styles = StyleSheet.create({
  surface: {position: 'absolute', inset: 0},
  pageTitle: {position: 'absolute', left: 172, top: 24, color: '#fff', ...shellTextStyle('SizeLarge')},
  viewport: {position: 'absolute', left: 172, top: 104, width: 1576, height: 976, overflow: 'hidden'},
  gridContent: {position: 'absolute', left: 0, width: 1576},
  sectionTitle: {position: 'absolute', left: 0, top: 24, height: 34, color: '#fff', ...shellTextStyle('SizeXSmall')},
  sortHeader: {position: 'absolute', right: 0, top: 24, width: 772, height: 34, color: 'rgba(255,255,255,0.7)', textAlign: 'right', ...shellTextStyle('Size3XSmall')},
  sortButton: {position: 'absolute', left: 52, top: 186, width: 72, height: 72, alignItems: 'center', justifyContent: 'center'},
  sortBar: {width: 34, height: 3, borderRadius: 2, backgroundColor: '#fff'},
  sortBarMiddle: {width: 25, marginTop: 7}, sortBarShort: {width: 16, marginTop: 7},
  tileSlot: {position: 'absolute', width: 296, height: 296},
  tile: {width: 296, height: 296, overflow: 'visible', backgroundColor: 'rgba(255,255,255,0.05)'},
  tileArt: {position: 'absolute', inset: 0, width: 296, height: 296},
  fallbackArt: {position: 'absolute', inset: 0, alignItems: 'center', justifyContent: 'center', backgroundColor: '#353535'},
  fallbackMark: {width: 64, height: 46, borderWidth: 3, borderRadius: 6, borderColor: 'rgba(255,255,255,0.47)'},
  tileGradient: {position: 'absolute', inset: 0, backgroundColor: 'rgba(0,0,0,0.66)'},
  tileGradientFocused: {opacity: 1}, tileGradientRest: {opacity: SHELL_LIBRARY_METRICS.gradientOpacityRest},
  tileMeta: {position: 'absolute', left: 16, right: 16, bottom: 16},
  tileTitle: {color: '#fff', ...shellTextStyle('Size2XSmall')},
  tileSubLabel: {marginTop: 8, color: 'rgba(255,255,255,0.7)', ...shellTextStyle('Size3XSmall')},
  sortPanel: {position: 'absolute', left: 52, top: 258, minWidth: 384, paddingVertical: 8, zIndex: 20, borderRadius: 16, backgroundColor: '#080a0f'},
  sortOption: {height: 72, minWidth: 384, paddingLeft: 72, paddingRight: 20, flexDirection: 'row', alignItems: 'center'},
  sortCheck: {position: 'absolute', left: 16, width: 40, color: '#fff', textAlign: 'center', ...shellTextStyle('SizeXSmall')},
  sortOptionText: {color: '#fff', ...shellTextStyle('SizeXSmall')},
  emptyHost: {position: 'absolute', left: 172, top: 104, width: 1576, height: 824, alignItems: 'center', justifyContent: 'center'},
  emptyHeading: {color: '#fff', ...shellTextStyle('SizeNormal')},
  emptyCopy: {width: 1040, marginTop: 16, marginBottom: 56, color: 'rgba(255,255,255,0.7)', textAlign: 'center', ...shellTextStyle('SizeXSmall')},
  emptyButton: {height: 72, minWidth: 334, maxWidth: 638, paddingHorizontal: 36, alignItems: 'center', justifyContent: 'center', borderRadius: 16, backgroundColor: 'rgba(255,255,255,0.05)'},
  emptyButtonText: {color: '#fff', ...shellTextStyle('SizeXSmall')},
});
