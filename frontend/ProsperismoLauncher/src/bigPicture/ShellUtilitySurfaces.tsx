import React, {useEffect, useMemo, useRef, useState} from 'react';
import {
  Animated,
  findNodeHandle,
  Image,
  Pressable,
  StyleSheet,
  Text,
  TextInput,
  UIManager,
  View,
} from 'react-native';
import type {GameInstall} from '../core/models';
import {
  SHELL_OVERLAY_METRICS,
  shellDialogButtonRowWidth,
  shellModalShowEase,
  shellSurfaceEase,
  shellUtilityWidth,
} from './shellSurfaces';
import {shellTextStyle} from './shellTypography';
import {ShellFocusOverlay} from './ShellFocusOverlay';

type FocusableUIManager = typeof UIManager & {focus?(reactTag: number): void};

function focusNative(target: unknown): void {
  if (typeof (target as {focus?(): void} | null)?.focus === 'function') {
    (target as {focus(): void}).focus();
    return;
  }
  const tag = findNodeHandle(target as any);
  const focus = (UIManager as FocusableUIManager).focus;
  if (tag !== null && typeof focus === 'function') {
    focus(tag);
  }
}

/** Neutral local-user avatar. It intentionally has no Sony account imagery. */
export function GenericAvatar({color = '#f5f7fa'}: {color?: any}) {
  return <View accessibilityElementsHidden importantForAccessibility="no-hide-descendants" style={styles.avatarGlyph}>
    <View style={[styles.avatarHead, {backgroundColor: color}]} />
    <View style={[styles.avatarShoulders, {backgroundColor: color}]} />
  </View>;
}

type PromptKind = 'confirm' | 'back' | 'options';

function PromptMark({kind}: {kind: PromptKind}) {
  if (kind === 'back') {
    return <View style={styles.promptCircle} />;
  }
  if (kind === 'options') {
    return <View style={styles.promptOptions}>{[0, 1, 2].map(index => <View key={index} style={styles.promptOptionsLine} />)}</View>;
  }
  return <View style={styles.promptCross}><View style={styles.promptCrossA} /><View style={styles.promptCrossB} /></View>;
}

export interface ButtonPrompt {
  kind: PromptKind;
  label: string;
}

export function ShellButtonPrompts({prompts}: {prompts: readonly ButtonPrompt[]}) {
  return <View pointerEvents="none" style={styles.promptBar}>
    {prompts.map(prompt => <View key={`${prompt.kind}-${prompt.label}`} style={styles.promptItem}>
      <PromptMark kind={prompt.kind} />
      <Text style={styles.promptLabel}>{prompt.label}</Text>
    </View>)}
  </View>;
}

export function SearchSurface({games, onClose, onLaunch}: {
  games: readonly GameInstall[];
  onClose(): void;
  onLaunch(game: GameInstall): void;
}) {
  const [query, setQuery] = useState('');
  const [selectedIndex, setSelectedIndex] = useState(0);
  const inputRef = useRef<TextInput>(null);
  const resultRefs = useRef<any[]>([]);
  const results = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase();
    if (!needle) {
      return games.slice(0, 8);
    }
    return games.filter(game => `${game.titleName} ${game.titleId}`.toLocaleLowerCase().includes(needle)).slice(0, 8);
  }, [games, query]);
  useEffect(() => {
    setSelectedIndex(0);
  }, [query]);
  useEffect(() => {
    inputRef.current?.focus();
  }, []);
  const activate = () => {
    const game = results[selectedIndex];
    if (game) {
      onLaunch(game);
    }
  };
  const onKeyDown = (event: any) => {
    const key = event?.nativeEvent?.key;
    if (key === 'Escape' || key === 'GamepadB') {
      onClose();
      event.stopPropagation?.();
      return;
    }
    if (key === 'ArrowDown' || key === 'GamepadDPadDown') {
      const next = Math.min(results.length - 1, selectedIndex + 4);
      setSelectedIndex(Math.max(0, next));
      focusNative(resultRefs.current[next]);
      event.stopPropagation?.();
      return;
    }
    if (key === 'ArrowUp' || key === 'GamepadDPadUp') {
      if (selectedIndex < 4) {
        inputRef.current?.focus();
      } else {
        const next = selectedIndex - 4;
        setSelectedIndex(next);
        focusNative(resultRefs.current[next]);
      }
      event.stopPropagation?.();
      return;
    }
    if (key === 'ArrowLeft' || key === 'GamepadDPadLeft' || key === 'ArrowRight' || key === 'GamepadDPadRight') {
      const delta = key === 'ArrowLeft' || key === 'GamepadDPadLeft' ? -1 : 1;
      const next = Math.max(0, Math.min(results.length - 1, selectedIndex + delta));
      setSelectedIndex(next);
      focusNative(resultRefs.current[next]);
      event.stopPropagation?.();
    }
  };
  return <View style={styles.fullSurface} {...({onKeyDownCapture: onKeyDown} as any)}>
    <Text style={styles.pageTitle}>Search</Text>
    <View style={styles.searchBox}>
      <View style={styles.searchGlyph}><View style={styles.searchLens} /><View style={styles.searchHandle} /></View>
      <TextInput
        ref={inputRef}
        accessibilityLabel="Search games"
        autoFocus
        onChangeText={setQuery}
        onSubmitEditing={activate}
        placeholder="Search games"
        placeholderTextColor="rgba(255,255,255,0.48)"
        selectionColor="#ffffff"
        style={styles.searchInput}
        value={query}
      />
    </View>
    <Text style={styles.resultHeading}>{query.trim() ? `${results.length} results` : 'Games'}</Text>
    <View style={styles.resultsGrid}>
      {results.map((game, index) => <Pressable
        ref={node => { resultRefs.current[index] = node; }}
        accessibilityRole="button"
        key={game.gamePath}
        onFocus={() => setSelectedIndex(index)}
        onPress={() => onLaunch(game)}
        style={[styles.resultTile, {left: (index % 4) * 402, top: Math.floor(index / 4) * 434}]}>
        {game.iconPath || game.artworkPath
          ? <Image resizeMode="cover" source={{uri: `file:///${(game.iconPath ?? game.artworkPath ?? '').replace(/\\/g, '/')}`}} style={styles.resultArt} />
          : <View style={styles.resultMonogram}><Text style={styles.resultMonogramText}>{game.titleName.slice(0, 1).toUpperCase()}</Text></View>}
        <ShellFocusOverlay active={selectedIndex === index} width={370} height={370} radius={16} />
        <Text numberOfLines={2} style={styles.resultTitle}>{game.titleName}</Text>
      </Pressable>)}
      {results.length === 0 && <Text style={styles.emptyText}>No games match “{query.trim()}”.</Text>}
    </View>
    <ShellButtonPrompts prompts={[{kind: 'confirm', label: 'Select'}, {kind: 'back', label: 'Back'}]} />
  </View>;
}

export function ProfileMenu({onClose, onDesktop}: {onClose(): void; onDesktop(): void}) {
  const [selectedIndex, setSelectedIndex] = useState(0);
  const refs = useRef<any[]>([]);
  const items = [
    {label: 'Desktop Mode', action: onDesktop},
    {label: 'Close', action: onClose},
  ];
  useEffect(() => {
    focusNative(refs.current[0]);
  }, []);
  const onKeyDown = (event: any) => {
    const key = event?.nativeEvent?.key;
    if (key === 'Escape' || key === 'GamepadB') {
      onClose();
      event.stopPropagation?.();
      return;
    }
    if (key === 'ArrowDown' || key === 'GamepadDPadDown' || key === 'ArrowUp' || key === 'GamepadDPadUp') {
      const next = key === 'ArrowDown' || key === 'GamepadDPadDown' ? Math.min(1, selectedIndex + 1) : Math.max(0, selectedIndex - 1);
      setSelectedIndex(next);
      focusNative(refs.current[next]);
      event.stopPropagation?.();
    }
  };
  return <View style={styles.menuLayer} {...({onKeyDownCapture: onKeyDown} as any)}>
    <Pressable accessibilityLabel="Close profile menu" onPress={onClose} style={styles.menuScrim} />
    <View style={styles.profilePanel}>
      <View style={styles.profileHeader}><View style={styles.profileAvatar}><GenericAvatar /></View><View><Text style={styles.profileName}>Local User</Text><Text style={styles.profileStatus}>Prosperismo</Text></View></View>
      <View style={styles.profileDivider} />
      {items.map((item, index) => <Pressable ref={node => { refs.current[index] = node; }} accessibilityRole="button" key={item.label} onFocus={() => setSelectedIndex(index)} onPress={item.action} style={styles.menuRow}><ShellFocusOverlay active={selectedIndex === index} width={636} height={90} radius={16} /><Text style={styles.menuRowText}>{item.label}</Text></Pressable>)}
    </View>
    <ShellButtonPrompts prompts={[{kind: 'confirm', label: 'Select'}, {kind: 'back', label: 'Back'}]} />
  </View>;
}

export interface ShellUtilityItem {
  label: string;
  glyph: string;
  onPress(): void;
}

/** HOME m171 UtilityContainer: 56px marks, 48px leading gaps, one label. */
export function ShellUtilityStrip({items, selectedIndex, onSelect}: {
  items: readonly ShellUtilityItem[];
  selectedIndex: number;
  onSelect(index: number): void;
}) {
  return <View style={[styles.utilityStrip, {width: shellUtilityWidth(items.length)}]}>
    {items.map((item, index) => {
      const focused = index === selectedIndex;
      return <Pressable
        accessibilityLabel={item.label}
        accessibilityRole="button"
        key={`${item.label}-${index}`}
        onFocus={() => onSelect(index)}
        onHoverIn={() => onSelect(index)}
        onPress={item.onPress}
        style={[styles.utilitySlot, focused ? styles.utilityFocused : styles.utilityResting]}>
        <View style={styles.utilityIcon}><Text style={styles.utilityGlyph}>{item.glyph}</Text></View>
        {focused && <Text numberOfLines={1} style={styles.utilityLabel}>{item.label}</Text>}
      </Pressable>;
    })}
  </View>;
}

export type ShellMenuEntry =
  | {kind: 'action'; id: string; label: string; glyph?: string; destructive?: boolean; onPress(): void}
  | {kind: 'separator'; id: string}
  | {kind: 'header'; id: string; label: string};

export interface ShellMenuAnchor {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** HOME OptionsMenu analogue with non-focusable headers and separators. */
export function ShellContextMenu({entries, selectedIndex, onSelect, onClose, anchor}: {
  entries: readonly ShellMenuEntry[];
  selectedIndex: number;
  onSelect(index: number): void;
  onClose(): void;
  anchor?: ShellMenuAnchor;
}) {
  const actionIndexes = entries.map((entry, index) => entry.kind === 'action' ? index : -1).filter(index => index >= 0);
  const move = (delta: number) => {
    const current = Math.max(0, actionIndexes.indexOf(selectedIndex));
    onSelect(actionIndexes[Math.max(0, Math.min(actionIndexes.length - 1, current + delta))] ?? -1);
  };
  const onKeyDown = (event: any) => {
    const key = event?.nativeEvent?.key;
    if (key === 'ArrowUp' || key === 'GamepadDPadUp') {
      move(-1);
    } else if (key === 'ArrowDown' || key === 'GamepadDPadDown') {
      move(1);
    } else if (key === 'Enter' || key === 'GamepadA') {
      const entry = entries[selectedIndex];
      if (entry?.kind === 'action') {
        entry.onPress();
      }
    } else if (key === 'Escape' || key === 'GamepadB') {
      onClose();
    } else {
      return;
    }
    event.stopPropagation?.();
  };
  return <View style={styles.menuLayer} {...({onKeyDownCapture: onKeyDown} as any)}>
    <Pressable accessibilityLabel="Close options menu" onPress={onClose} style={styles.menuScrim} />
    <View style={[
      styles.shellContextPanel,
      anchor && {left: anchor.x + anchor.width - 3, right: undefined, top: anchor.y + 3},
    ]}>
      {entries.map((entry, index) => entry.kind === 'separator'
        ? <View key={entry.id} style={styles.shellMenuSeparator} />
        : entry.kind === 'header'
          ? <Text key={entry.id} style={styles.shellMenuHeader}>{entry.label}</Text>
          : <Pressable
            accessibilityRole="button"
            key={entry.id}
            onFocus={() => onSelect(index)}
            onHoverIn={() => onSelect(index)}
            onPress={entry.onPress}
            style={styles.shellMenuRow}>
            <ShellFocusOverlay active={selectedIndex === index} width={652} height={98} radius={16} />
            <View style={styles.shellMenuIcon}><Text style={[styles.shellMenuGlyph, entry.destructive && styles.destructiveText]}>{entry.glyph ?? ''}</Text></View>
            <Text style={[styles.shellMenuText, entry.destructive && styles.destructiveText]}>{entry.label}</Text>
          </Pressable>)}
    </View>
    <ShellButtonPrompts prompts={[{kind: 'confirm', label: 'Select'}, {kind: 'back', label: 'Back'}]} />
  </View>;
}

export interface ShellDialogButtonModel {
  id: string;
  label: string;
  onPress(): void;
}

/** Recovered DIALOG body/button stack. Positive action should be last. */
export function ShellDialogSurface({title, body, errorCode, buttons, fullScreen = false, dismissable = true, onDismiss}: {
  title: string;
  body: string;
  errorCode?: string;
  buttons: readonly ShellDialogButtonModel[];
  fullScreen?: boolean;
  dismissable?: boolean;
  onDismiss(): void;
}) {
  const [focusedIndex, setFocusedIndex] = useState(Math.max(0, buttons.length - 1));
  const reveal = useRef(new Animated.Value(0)).current;
  useEffect(() => {
    const animation = Animated.timing(reveal, {
      toValue: 1,
      duration: SHELL_OVERLAY_METRICS.motion.modalShowMs,
      delay: SHELL_OVERLAY_METRICS.motion.modalShowDelayMs,
      easing: shellModalShowEase,
      useNativeDriver: true,
    });
    animation.start();
    return () => animation.stop();
  }, [reveal]);
  const onKeyDown = (event: any) => {
    const key = event?.nativeEvent?.key;
    if (key === 'ArrowLeft' || key === 'GamepadDPadLeft') {
      setFocusedIndex(index => Math.max(0, index - 1));
    } else if (key === 'ArrowRight' || key === 'GamepadDPadRight') {
      setFocusedIndex(index => Math.min(buttons.length - 1, index + 1));
    } else if (key === 'Enter' || key === 'GamepadA') {
      buttons[focusedIndex]?.onPress();
    } else if ((key === 'Escape' || key === 'GamepadB') && dismissable) {
      onDismiss();
    } else {
      return;
    }
    event.stopPropagation?.();
  };
  const bodyHeight = fullScreen
    ? SHELL_OVERLAY_METRICS.dialog.fullScreenBodyHeight
    : SHELL_OVERLAY_METRICS.dialog.popupBodyHeight;
  const bodyTop = fullScreen
    ? SHELL_OVERLAY_METRICS.dialog.top
    : (SHELL_OVERLAY_METRICS.dialog.designHeight - bodyHeight - SHELL_OVERLAY_METRICS.dialog.buttonRowGap - SHELL_OVERLAY_METRICS.dialog.buttonHeight) / 2;
  const contentWidth = shellDialogButtonRowWidth(buttons.length);
  return <View style={styles.dialogLayer} {...({onKeyDownCapture: onKeyDown} as any)}>
    <View style={styles.dialogScrim} />
    <Animated.View style={[styles.dialogBody, {height: bodyHeight, top: bodyTop, opacity: reveal, transform: [{translateY: reveal.interpolate({inputRange: [0, 1], outputRange: [SHELL_OVERLAY_METRICS.motion.riseDistance, 0]})}]}]}>
      <View style={styles.dialogHeader}>
        <Text numberOfLines={1} style={styles.dialogTitle}>{title}</Text>
        {!!errorCode && <Text style={styles.dialogError}>{errorCode}</Text>}
      </View>
      <View style={styles.dialogMessageHost}><Text style={styles.dialogMessage}>{body}</Text></View>
    </Animated.View>
    <View style={[styles.dialogButtons, {top: bodyTop + bodyHeight + SHELL_OVERLAY_METRICS.dialog.buttonRowGap, width: contentWidth, left: (1920 - contentWidth) / 2}]}>
      {buttons.map((button, index) => <Pressable
        accessibilityRole="button"
        key={button.id}
        onFocus={() => setFocusedIndex(index)}
        onHoverIn={() => setFocusedIndex(index)}
        onPress={button.onPress}
        style={[styles.dialogButton, index === buttons.length - 1 && styles.dialogButtonLast]}>
        <ShellFocusOverlay active={focusedIndex === index} width={384} height={72} radius={16} />
        <Text style={styles.dialogButtonText}>{button.label}</Text>
      </Pressable>)}
    </View>
  </View>;
}

/** Whole-scene 300ms transition used when Home and Settings exchange routes. */
export function ShellSurfaceTransition({children}: {children: React.ReactNode}) {
  const phase = useRef(new Animated.Value(0)).current;
  useEffect(() => {
    const animation = Animated.timing(phase, {
      toValue: 1,
      duration: SHELL_OVERLAY_METRICS.motion.screenMs,
      easing: shellSurfaceEase,
      useNativeDriver: true,
    });
    animation.start();
    return () => animation.stop();
  }, [phase]);
  return <Animated.View style={[styles.transitionSurface, {opacity: phase}]}>{children}</Animated.View>;
}

const styles = StyleSheet.create({
  avatarGlyph: {width: 40, height: 40, alignItems: 'center', justifyContent: 'center', overflow: 'hidden'},
  avatarHead: {position: 'absolute', top: 5, width: 14, height: 14, borderRadius: 7},
  avatarShoulders: {position: 'absolute', top: 22, width: 30, height: 22, borderRadius: 15},
  promptBar: {position: 'absolute', right: 84, bottom: 42, height: 36, flexDirection: 'row', alignItems: 'center', gap: 32},
  promptItem: {flexDirection: 'row', alignItems: 'center'},
  promptLabel: {color: '#fff', marginLeft: 10, ...shellTextStyle('Size3XSmall')},
  promptCircle: {width: 20, height: 20, borderWidth: 2, borderRadius: 10, borderColor: '#fff'},
  promptCross: {width: 20, height: 20},
  promptCrossA: {position: 'absolute', left: 9, top: 0, width: 2, height: 20, backgroundColor: '#fff', transform: [{rotate: '45deg'}]},
  promptCrossB: {position: 'absolute', left: 9, top: 0, width: 2, height: 20, backgroundColor: '#fff', transform: [{rotate: '-45deg'}]},
  promptOptions: {width: 22, height: 18, justifyContent: 'space-between', paddingVertical: 2},
  promptOptionsLine: {height: 2, width: 22, borderRadius: 1, backgroundColor: '#fff'},
  fullSurface: {position: 'absolute', inset: 0, zIndex: 15, backgroundColor: 'rgba(2,4,8,0.96)'},
  pageTitle: {position: 'absolute', left: 172, top: 76, color: '#fff', ...shellTextStyle('SizeXLarge')},
  searchBox: {position: 'absolute', left: 172, top: 166, width: 1576, height: 72, borderRadius: 16, flexDirection: 'row', alignItems: 'center', backgroundColor: 'rgba(255,255,255,0.12)', borderWidth: 2, borderColor: 'rgba(255,255,255,0.5)'},
  searchGlyph: {width: 34, height: 34, marginLeft: 24, marginRight: 20},
  searchLens: {position: 'absolute', left: 3, top: 3, width: 20, height: 20, borderWidth: 3, borderRadius: 10, borderColor: '#fff'},
  searchHandle: {position: 'absolute', left: 22, top: 23, width: 13, height: 3, borderRadius: 2, backgroundColor: '#fff', transform: [{rotate: '47deg'}]},
  searchInput: {flex: 1, height: 68, paddingVertical: 0, paddingRight: 24, color: '#fff', ...shellTextStyle('SizeNormal')},
  resultHeading: {position: 'absolute', left: 172, top: 270, color: 'rgba(255,255,255,0.7)', ...shellTextStyle('Size2XSmall')},
  resultsGrid: {position: 'absolute', left: 172, top: 318, width: 1576, height: 762, overflow: 'hidden'},
  resultTile: {position: 'absolute', width: 370, height: 430, overflow: 'visible'},
  resultArt: {position: 'absolute', left: 0, top: 0, width: 370, height: 370, borderRadius: 16},
  resultMonogram: {width: 370, height: 370, borderRadius: 16, backgroundColor: 'rgba(255,255,255,0.12)', alignItems: 'center', justifyContent: 'center'},
  resultMonogramText: {color: '#fff', ...shellTextStyle('SizeXLarge', '600')},
  resultTitle: {position: 'absolute', left: 0, top: 382, width: 370, height: 48, color: '#fff', ...shellTextStyle('SizeXSmall')},
  emptyText: {marginTop: 60, color: 'rgba(255,255,255,0.7)', ...shellTextStyle('SizeXSmall')},
  menuLayer: {position: 'absolute', inset: 0, zIndex: 25}, menuScrim: {position: 'absolute', inset: 0, backgroundColor: 'rgba(0,0,0,0.65)'},
  profilePanel: {position: 'absolute', top: 126, left: 1188, width: 652, minHeight: 306, maxHeight: 810, borderRadius: 16, overflow: 'hidden', padding: 8, backgroundColor: '#080a0f'},
  profileHeader: {height: 104, paddingHorizontal: 24, flexDirection: 'row', alignItems: 'center'}, profileAvatar: {width: 64, height: 64, borderRadius: 32, marginRight: 20, backgroundColor: '#39404a', alignItems: 'center', justifyContent: 'center'},
  profileName: {color: '#fff', ...shellTextStyle('SizeXSmall')}, profileStatus: {marginTop: 3, color: 'rgba(255,255,255,0.62)', ...shellTextStyle('Size4XSmall')}, profileDivider: {height: 2, marginHorizontal: 8, marginBottom: 2, backgroundColor: 'rgba(255,255,255,0.1)'},
  menuRow: {height: 90, paddingHorizontal: 24, justifyContent: 'center'}, menuRowText: {color: '#fff', ...shellTextStyle('SizeXSmall')},
  utilityStrip: {height: 128, marginTop: 8, flexDirection: 'row', overflow: 'visible'},
  utilitySlot: {width: 56, height: 56, marginLeft: 48, alignItems: 'center', overflow: 'visible'},
  utilityFocused: {opacity: 1}, utilityResting: {opacity: SHELL_OVERLAY_METRICS.utility.unfocusedOpacity},
  utilityIcon: {width: 56, height: 56, borderRadius: 28, alignItems: 'center', justifyContent: 'center', backgroundColor: 'rgba(255,255,255,0.12)'},
  utilityGlyph: {color: '#fff', ...shellTextStyle('SizeXSmall')},
  utilityLabel: {position: 'absolute', top: 72, left: -140, width: 336, color: '#fff', textAlign: 'center', ...shellTextStyle('SizeXSmall')},
  shellContextPanel: {position: 'absolute', right: 80, top: 126, minWidth: 652, maxWidth: 784, paddingVertical: 8, overflow: 'hidden', borderRadius: 16, backgroundColor: '#080a0f'},
  shellMenuRow: {height: 98, flexDirection: 'row', alignItems: 'center'},
  shellMenuIcon: {width: 72, height: 98, alignItems: 'center', justifyContent: 'center'},
  shellMenuGlyph: {color: '#fff', textAlign: 'center', ...shellTextStyle('SizeXSmall')},
  shellMenuText: {flex: 1, paddingRight: 24, color: '#fff', ...shellTextStyle('SizeXSmall')},
  destructiveText: {color: '#ff7b7b'},
  shellMenuSeparator: {height: 2, marginHorizontal: 16, marginTop: 16, backgroundColor: 'rgba(255,255,255,0.1)'},
  shellMenuHeader: {height: 40, marginTop: 16, marginBottom: 5, paddingLeft: 24, color: 'rgba(255,255,255,0.7)', textAlignVertical: 'center', ...shellTextStyle('SizeSmall')},
  dialogLayer: {position: 'absolute', inset: 0, zIndex: 20000},
  dialogScrim: {position: 'absolute', inset: 0, backgroundColor: 'rgba(0,0,0,0.8)'},
  dialogBody: {position: 'absolute', left: 304, width: 1312, paddingHorizontal: 48, paddingVertical: 24, borderWidth: 1, borderColor: 'rgba(255,255,255,0.1)', borderRadius: 16, backgroundColor: '#080a0f'},
  dialogHeader: {height: 64, flexDirection: 'row', alignItems: 'center'},
  dialogTitle: {flex: 1, color: '#fff', ...shellTextStyle('SizeLarge')},
  dialogError: {paddingLeft: 48, opacity: 0.7, color: '#fff', ...shellTextStyle('SizeXSmall')},
  dialogMessageHost: {flex: 1, alignItems: 'center', justifyContent: 'center'},
  dialogMessage: {maxWidth: 920, color: '#fff', textAlign: 'center', ...shellTextStyle('SizeNormal')},
  dialogButtons: {position: 'absolute', height: 72, flexDirection: 'row'},
  dialogButton: {width: 384, height: 72, marginRight: 16, borderRadius: 16, alignItems: 'center', justifyContent: 'center', backgroundColor: 'rgba(255,255,255,0.05)'},
  dialogButtonLast: {marginRight: 0},
  dialogButtonText: {color: '#fff', ...shellTextStyle('SizeNormal')},
  transitionSurface: {position: 'absolute', inset: 0},
});
