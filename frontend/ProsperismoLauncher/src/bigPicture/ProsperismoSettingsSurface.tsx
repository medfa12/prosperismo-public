import React, {useEffect, useRef, useState} from 'react';
import {findNodeHandle, Pressable, StyleSheet, Text, UIManager, View} from 'react-native';
import type {LauncherSettings} from '../core/models';
import {SHELL_SETTINGS_METRICS} from './shellSurfaces';
import {shellTextStyle} from './shellTypography';
import {ShellFocusOverlay} from './ShellFocusOverlay';

export const PROSPERISMO_SETTINGS_CATEGORIES = [
  ['General', 'Game folders, library order, and launcher behavior'],
  ['Graphics', 'Resolution, presentation, and Vulkan diagnostics'],
  ['Audio and Interface', 'Controller and keyboard input'],
  ['Emulation', 'Shaders and compatibility defaults'],
  ['Logging', 'Shader, command-buffer, and printf output'],
  ['Environment', 'Title patches and compatibility profiles'],
  ['About Prosperismo', 'Version, diagnostics, and legal notices'],
] as const;

const LIBRARY_SORT_FIELDS = ['titleName', 'titleId', 'gameVersion', 'firmwareVersion', 'gamePath', 'status', 'comment'] as const;
const SHADER_OPTIMIZATIONS = ['None', 'Size', 'Performance'] as const;

type FocusableUIManager = typeof UIManager & {focus?(reactTag: number): void};
type SettingRow = {label: string; value: string; onPress?: () => void};

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

function nextValue<T>(values: readonly T[], value: T): T {
  return values[(values.indexOf(value) + 1) % values.length];
}

function SettingsFocus({active, width, height}: {active: boolean; width: number; height: number}) {
  return <ShellFocusOverlay active={active} width={width} height={height} radius={0} crop={{top: 3, bottom: 5}} />;
}

function CategoryGlyph({index}: {index: number}) {
  return <View style={styles.categoryGlyph}>
    <View style={[styles.glyphMark, index % 3 === 0 && styles.glyphRound, index % 3 === 1 && styles.glyphDiamond]} />
    {index % 3 === 2 && <View style={styles.glyphInner} />}
  </View>;
}

export function ProsperismoSettingsRoot({selectedIndex, onSelect, onActivate, onRef}: {
  selectedIndex: number;
  onSelect(index: number): void;
  onActivate(index: number): void;
  onRef(index: number, node: any): void;
}) {
  return <View style={styles.stage}>
    <Text style={styles.pageTitle}>Settings</Text>
    <View style={styles.categoryList}>
      {PROSPERISMO_SETTINGS_CATEGORIES.map(([name, description], index) => <Pressable
        ref={node => onRef(index, node)}
        accessibilityLabel={`${name}. ${description}`}
        accessibilityRole="button"
        key={name}
        onFocus={() => onSelect(index)}
        onPress={() => onActivate(index)}
        style={styles.categoryRow}>
        <SettingsFocus active={index === selectedIndex} width={SHELL_SETTINGS_METRICS.listWidth} height={SHELL_SETTINGS_METRICS.capturedRowPitch} />
        <CategoryGlyph index={index} />
        <View style={styles.categoryCopy}>
          <Text style={styles.categoryTitle}>{name}</Text>
        </View>
        <Text style={styles.chevron}>›</Text>
      </Pressable>)}
    </View>
  </View>;
}

export function ProsperismoSettingsDetail({categoryIndex, settings, onSave, onBack}: {
  categoryIndex: number;
  settings: LauncherSettings;
  onSave(next: LauncherSettings): void;
  onBack(): void;
}) {
  const [activeCategory, setActiveCategory] = useState(categoryIndex);
  const [focusedIndex, setFocusedIndex] = useState(0);
  const [focusColumn, setFocusColumn] = useState<'tabs' | 'rows'>('rows');
  const refs = useRef<any[]>([]);
  const category = PROSPERISMO_SETTINGS_CATEGORIES[activeCategory] ?? PROSPERISMO_SETTINGS_CATEGORIES[0];
  const updateGlobal = <K extends keyof LauncherSettings['global']>(key: K, value: LauncherSettings['global'][K]) => onSave({...settings, global: {...settings.global, [key]: value}});
  const rows: SettingRow[] = activeCategory === 0 ? [
    {label: 'Game folders', value: `${settings.gameDirectories.length} configured`},
    {label: 'Library sort', value: settings.library.sortField, onPress: () => onSave({...settings, library: {...settings.library, sortField: nextValue(LIBRARY_SORT_FIELDS, settings.library.sortField)}})},
    {label: 'Sort direction', value: settings.library.sortDirection, onPress: () => onSave({...settings, library: {...settings.library, sortDirection: settings.library.sortDirection === 'ascending' ? 'descending' : 'ascending'}})},
  ] : activeCategory === 1 ? [
    {label: 'Resolution', value: settings.global.screenResolution, onPress: () => updateGlobal('screenResolution', settings.global.screenResolution === '1280x720' ? '1920x1080' : '1280x720')},
    {label: 'Vblank frequency', value: `${settings.global.vblankFrequency} Hz`, onPress: () => updateGlobal('vblankFrequency', settings.global.vblankFrequency === 60 ? 120 : 60)},
    {label: 'Vulkan validation', value: settings.global.vulkanValidation ? 'On' : 'Off', onPress: () => updateGlobal('vulkanValidation', !settings.global.vulkanValidation)},
    {label: 'RenderDoc', value: settings.global.renderDoc ? 'On' : 'Off', onPress: () => updateGlobal('renderDoc', !settings.global.renderDoc)},
  ] : activeCategory === 2 ? [
    {label: 'Controller mapping', value: 'Windows host'},
    {label: 'Keyboard input', value: 'Windows host'},
  ] : activeCategory === 3 ? [
    {label: 'Shader optimization', value: settings.global.shaderOptimization, onPress: () => updateGlobal('shaderOptimization', nextValue(SHADER_OPTIMIZATIONS, settings.global.shaderOptimization))},
    {label: 'Shader validation', value: settings.global.shaderValidation ? 'On' : 'Off', onPress: () => updateGlobal('shaderValidation', !settings.global.shaderValidation)},
    {label: 'NGG rectlist draw', value: settings.global.nggRectlistDraw ? 'On' : 'Off', onPress: () => updateGlobal('nggRectlistDraw', !settings.global.nggRectlistDraw)},
  ] : activeCategory === 4 ? [
    {label: 'Shader log direction', value: settings.global.shaderLogDirection, onPress: () => updateGlobal('shaderLogDirection', nextValue(['Silent', 'Console', 'File'] as const, settings.global.shaderLogDirection))},
    {label: 'Shader log folder', value: settings.global.shaderLogFolder},
    {label: 'Buffer dump', value: settings.global.commandBufferDump ? 'On' : 'Off', onPress: () => updateGlobal('commandBufferDump', !settings.global.commandBufferDump)},
    {label: 'Printf output', value: settings.global.printfDirection, onPress: () => updateGlobal('printfDirection', nextValue(['Silent', 'Console', 'File'] as const, settings.global.printfDirection))},
  ] : activeCategory === 5 ? [
    {label: 'Patch titles', value: `${Object.keys(settings.patchSelections).length} configured`},
    {label: 'Compatibility profiles', value: `${Object.keys(settings.compatibility).length} imported`},
  ] : [
    {label: 'Prosperismo', value: 'React Native Windows shell'},
    {label: 'Presentation', value: 'Firmware-derived contracts'},
  ];
  useEffect(() => {
    setActiveCategory(categoryIndex);
    setFocusedIndex(0);
    setFocusColumn('rows');
    focusNative(refs.current[0]);
  }, [categoryIndex]);
  const onKeyDown = (event: any) => {
    const key = event?.nativeEvent?.key;
    if (key === 'Escape' || key === 'GamepadB') {
      onBack();
      event.stopPropagation?.();
      return;
    }
    if ((key === 'ArrowLeft' || key === 'GamepadDPadLeft') && focusColumn === 'rows') {
      setFocusColumn('tabs');
      event.stopPropagation?.();
      return;
    }
    if ((key === 'ArrowRight' || key === 'GamepadDPadRight') && focusColumn === 'tabs') {
      setFocusColumn('rows');
      focusNative(refs.current[focusedIndex]);
      event.stopPropagation?.();
      return;
    }
    if (key === 'ArrowDown' || key === 'GamepadDPadDown' || key === 'ArrowUp' || key === 'GamepadDPadUp') {
      const delta = key === 'ArrowDown' || key === 'GamepadDPadDown' ? 1 : -1;
      if (focusColumn === 'tabs') {
        setActiveCategory(index => Math.max(0, Math.min(PROSPERISMO_SETTINGS_CATEGORIES.length - 1, index + delta)));
        setFocusedIndex(0);
      } else {
        const next = Math.max(0, Math.min(rows.length - 1, focusedIndex + delta));
        setFocusedIndex(next);
        focusNative(refs.current[next]);
      }
      event.stopPropagation?.();
    }
  };
  return <View style={styles.stage} {...({onKeyDownCapture: onKeyDown} as any)}>
    <Pressable accessibilityRole="button" onPress={onBack} style={styles.backTarget}><Text style={styles.backText}>‹ Settings</Text></Pressable>
    <Text style={styles.detailTitle}>{category[0]}</Text>
    <View style={styles.detailTabs}>
      {PROSPERISMO_SETTINGS_CATEGORIES.map(([label], index) => <Pressable
        accessibilityRole="tab"
        key={label}
        onFocus={() => { setFocusColumn('tabs'); setActiveCategory(index); setFocusedIndex(0); }}
        onPress={() => { setActiveCategory(index); setFocusedIndex(0); setFocusColumn('rows'); }}
        style={styles.detailTab}>
        <SettingsFocus active={focusColumn === 'tabs' && activeCategory === index} width={SHELL_SETTINGS_METRICS.tabWidth} height={SHELL_SETTINGS_METRICS.capturedTabPitch} />
        <Text style={[styles.detailTabText, activeCategory !== index && styles.detailTabTextDim]}>{label}</Text>
      </Pressable>)}
    </View>
    <View style={styles.detailList}>
      {rows.map((row, index) => <Pressable
        ref={node => { refs.current[index] = node; }}
        accessibilityRole="button"
        key={row.label}
        onFocus={() => setFocusedIndex(index)}
        onPress={row.onPress}
        style={styles.detailRow}>
        <SettingsFocus active={focusColumn === 'rows' && focusedIndex === index} width={SHELL_SETTINGS_METRICS.tabPanelWidth} height={SHELL_SETTINGS_METRICS.detailRowPitch} />
        <Text style={styles.detailLabel}>{row.label}</Text>
        <Text numberOfLines={1} style={styles.detailValue}>{row.value}</Text>
      </Pressable>)}
    </View>
  </View>;
}

const styles = StyleSheet.create({
  stage: {position: 'absolute', inset: 0},
  pageTitle: {position: 'absolute', left: 96, top: 82, color: '#fff', ...shellTextStyle('SizeLarge')},
  categoryList: {position: 'absolute', left: SHELL_SETTINGS_METRICS.listLeft, top: SHELL_SETTINGS_METRICS.listTop, width: SHELL_SETTINGS_METRICS.listWidth, height: SHELL_SETTINGS_METRICS.listHeight},
  categoryRow: {height: SHELL_SETTINGS_METRICS.capturedRowPitch, paddingLeft: SHELL_SETTINGS_METRICS.titleMarginLeft, paddingRight: SHELL_SETTINGS_METRICS.titleMarginRight, flexDirection: 'row', alignItems: 'center'},
  categoryGlyph: {width: SHELL_SETTINGS_METRICS.iconSize, height: SHELL_SETTINGS_METRICS.iconSize, marginRight: SHELL_SETTINGS_METRICS.imageMarginRight, alignItems: 'center', justifyContent: 'center'},
  glyphMark: {width: 28, height: 28, borderWidth: 3, borderColor: '#fff'}, glyphRound: {borderRadius: 14}, glyphDiamond: {transform: [{rotate: '45deg'}]}, glyphInner: {position: 'absolute', width: 10, height: 10, borderRadius: 5, backgroundColor: '#fff'},
  categoryCopy: {flex: 1}, categoryTitle: {color: '#fff', ...shellTextStyle('SizeNormal')}, categoryDescription: {marginTop: 5, color: 'rgba(255,255,255,0.7)', ...shellTextStyle('Size3XSmall')}, chevron: {width: 52, color: '#fff', textAlign: 'center', ...shellTextStyle('SizeXLarge')},
  backTarget: {position: 'absolute', left: 96, top: 38, paddingVertical: 10, paddingRight: 28}, backText: {color: 'rgba(255,255,255,0.72)', ...shellTextStyle('Size2XSmall')},
  detailTitle: {position: 'absolute', left: 96, top: 82, color: '#fff', ...shellTextStyle('SizeLarge')}, detailDescription: {position: 'absolute', left: 304, top: 166, color: 'rgba(255,255,255,0.7)', ...shellTextStyle('Size2XSmall')},
  detailTabs: {position: 'absolute', left: SHELL_SETTINGS_METRICS.tabLeft, top: SHELL_SETTINGS_METRICS.tabTop, width: SHELL_SETTINGS_METRICS.tabWidth, height: SHELL_SETTINGS_METRICS.tabPanelHeight},
  detailTab: {height: SHELL_SETTINGS_METRICS.capturedTabPitch, paddingHorizontal: 20, justifyContent: 'center'},
  detailTabText: {color: '#fff', ...shellTextStyle('SizeNormal')}, detailTabTextDim: {opacity: 0.7},
  detailList: {position: 'absolute', left: SHELL_SETTINGS_METRICS.tabLeft + SHELL_SETTINGS_METRICS.tabWidth + SHELL_SETTINGS_METRICS.tabPanelLeft, top: SHELL_SETTINGS_METRICS.tabTop, width: SHELL_SETTINGS_METRICS.tabPanelWidth, height: SHELL_SETTINGS_METRICS.tabPanelHeight, overflow: 'hidden'},
  detailRow: {height: SHELL_SETTINGS_METRICS.detailRowPitch, borderRadius: 16, paddingLeft: 16, paddingRight: 48, flexDirection: 'row', alignItems: 'center'}, detailLabel: {flex: 1, color: '#fff', ...shellTextStyle('SizeSmall')}, detailValue: {maxWidth: 540, marginRight: 16, opacity: SHELL_SETTINGS_METRICS.longTextValueOpacity, color: '#fff', textAlign: 'right', ...shellTextStyle('SizeXSmall')},
});
