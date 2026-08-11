import React, {useEffect, useRef} from 'react';
import {Animated, Easing, Image, Pressable, StyleSheet, Text, View} from 'react-native';
import {shellTextStyle} from './shellTypography';
import {SHELL_OVERLAY_METRICS} from './shellSurfaces';

export interface ShellFunctionPanelItem {
  id: string;
  title: string;
  glyph?: string;
  enabled?: boolean;
  onPress(): void;
}

export function ShellFunctionPanel({header, headerGlyph, items, selectedIndex, onSelect}: {
  header?: string;
  headerGlyph?: string;
  items: readonly ShellFunctionPanelItem[];
  selectedIndex: number;
  onSelect(index: number): void;
}) {
  const height = Math.max(
    SHELL_OVERLAY_METRICS.functionPanel.minHeight,
    Math.min(
      SHELL_OVERLAY_METRICS.functionPanel.maxHeight,
      SHELL_OVERLAY_METRICS.functionPanel.headerHeight + items.length * SHELL_OVERLAY_METRICS.functionPanel.rowHeight,
    ),
  );
  return <View style={[styles.functionPanel, {height}]}>
    {!!header && <View style={styles.functionHeader}>
      {!!headerGlyph && <View style={styles.functionHeaderIcon}><Text style={styles.functionGlyph}>{headerGlyph}</Text></View>}
      <Text numberOfLines={1} style={styles.functionHeaderText}>{header}</Text>
    </View>}
    <View style={styles.functionList}>
      {items.map((item, index) => <Pressable
        accessibilityRole="button"
        disabled={item.enabled === false}
        key={item.id}
        onFocus={() => onSelect(index)}
        onHoverIn={() => onSelect(index)}
        onPress={item.onPress}
        style={[styles.functionItem, item.enabled === false && styles.disabled]}>
        {selectedIndex === index && <View pointerEvents="none" style={styles.containerFocus} />}
        <Text numberOfLines={2} style={styles.functionItemText}>{item.title}</Text>
        {!!item.glyph && <View style={styles.functionRightIcon}><Text style={styles.functionGlyph}>{item.glyph}</Text></View>}
      </Pressable>)}
    </View>
  </View>;
}

export interface ShellFunctionRowItem {
  id: string;
  title: string;
  glyph?: string;
  artUri?: string;
  onPress(): void;
}

export function ShellFunctionRow({items, selectedIndex, focused, onSelect}: {
  items: readonly ShellFunctionRowItem[];
  selectedIndex: number;
  focused: boolean;
  onSelect(index: number): void;
}) {
  return <View style={styles.functionRow}>
    {items.map((item, index) => {
      const selected = index === selectedIndex;
      return <Pressable
        accessibilityRole="button"
        key={item.id}
        onFocus={() => onSelect(index)}
        onHoverIn={() => onSelect(index)}
        onPress={item.onPress}
        style={[styles.functionTileSlot, selected ? styles.functionTileSlotFocused : styles.functionTileSlotResting]}>
        <View style={[styles.functionTile, selected ? styles.functionTileFocused : styles.functionTileResting]}>
          {item.artUri ? <Image resizeMode="cover" source={{uri: item.artUri}} style={styles.fill} /> : <Text style={styles.functionTileGlyph}>{item.glyph ?? ''}</Text>}
          {selected && focused && <View pointerEvents="none" style={styles.functionTileFocus} />}
        </View>
        {selected && <Text numberOfLines={1} style={styles.functionCaption}>{item.title}</Text>}
      </Pressable>;
    })}
  </View>;
}

export interface ShellHubNavItem {id: string; label: string; onPress(): void}

export function ShellHubNav({items, selectedIndex, vertical = false, onSelect}: {
  items: readonly ShellHubNavItem[];
  selectedIndex: number;
  vertical?: boolean;
  onSelect(index: number): void;
}) {
  return <View style={vertical ? styles.hubNavVertical : styles.hubNavHorizontal}>
    {items.map((item, index) => <Pressable
      key={item.id}
      onFocus={() => onSelect(index)}
      onPress={item.onPress}
      style={vertical ? styles.hubNavVerticalItem : styles.hubNavHorizontalItem}>
      {index === selectedIndex && <View pointerEvents="none" style={styles.containerFocus} />}
      <Text style={[styles.hubNavText, index !== selectedIndex && styles.dim]}>{item.label}</Text>
    </Pressable>)}
  </View>;
}

export interface ShellSceneItem {id: string; title: string; artUri?: string; onPress(): void}
export interface ShellScene {id: string; heading: string; items: readonly ShellSceneItem[]}

/** HOME SceneList: vertical scenes; horizontal navigation stays inside a row. */
export function ShellSceneList({scenes, selectedScene, selectedItem, onSelect}: {
  scenes: readonly ShellScene[];
  selectedScene: number;
  selectedItem: number;
  onSelect(sceneIndex: number, itemIndex: number): void;
}) {
  return <View style={styles.sceneList}>
    {scenes.map((scene, sceneIndex) => <View key={scene.id} style={styles.scene}>
      <Text style={styles.sceneHeading}>{scene.heading}</Text>
      <View style={styles.sceneRow}>
        {scene.items.map((item, itemIndex) => <Pressable
          accessibilityRole="button"
          key={item.id}
          onFocus={() => onSelect(sceneIndex, itemIndex)}
          onHoverIn={() => onSelect(sceneIndex, itemIndex)}
          onPress={item.onPress}
          style={styles.sceneTile}>
          {item.artUri ? <Image resizeMode="cover" source={{uri: item.artUri}} style={styles.fill} /> : <View style={styles.sceneFallback} />}
          {sceneIndex === selectedScene && itemIndex === selectedItem && <View pointerEvents="none" style={styles.sceneTileFocus} />}
          <Text numberOfLines={1} style={styles.sceneTileLabel}>{item.title}</Text>
        </Pressable>)}
      </View>
    </View>)}
  </View>;
}

export function ShellHubViewer({title, tag, iconUri, children}: {
  title: string;
  tag?: string;
  iconUri?: string;
  children: React.ReactNode;
}) {
  return <View style={styles.hubViewer}>
    <View style={styles.hubHeader}>
      {iconUri ? <Image resizeMode="cover" source={{uri: iconUri}} style={styles.hubIcon} /> : <View style={styles.hubIconFallback} />}
      <ShellMarqueeText active text={title} width={520} scrollDistance={Math.max(0, title.length * 14 - 520)} />
      {!!tag && <><View style={styles.hubSeparator} /><Text style={styles.hubTag}>{tag}</Text></>}
    </View>
    <View style={styles.hubBody}>{children}</View>
  </View>;
}

/** Native marquee cycle: 2s dwell, 60px/s, fade-out/snap/fade-in. */
export function ShellMarqueeText({text, active, width, scrollDistance}: {
  text: string;
  active: boolean;
  width: number;
  scrollDistance: number;
}) {
  const offset = useRef(new Animated.Value(0)).current;
  const opacity = useRef(new Animated.Value(1)).current;
  useEffect(() => {
    offset.setValue(0);
    opacity.setValue(1);
    if (!active || scrollDistance <= 0) {
      return;
    }
    const travelMs = scrollDistance / (1000 / SHELL_OVERLAY_METRICS.marquee.frameMs) * 1000;
    const cycle = Animated.loop(Animated.sequence([
      Animated.delay(SHELL_OVERLAY_METRICS.marquee.dwellMs),
      Animated.timing(offset, {toValue: -scrollDistance, duration: travelMs, easing: Easing.linear, useNativeDriver: true}),
      Animated.timing(opacity, {toValue: 0, duration: SHELL_OVERLAY_METRICS.marquee.fadeOutMs, easing: Easing.linear, useNativeDriver: true}),
      Animated.timing(offset, {toValue: 0, duration: 0, useNativeDriver: true}),
      Animated.timing(opacity, {toValue: 1, duration: SHELL_OVERLAY_METRICS.marquee.fadeInMs, easing: Easing.linear, useNativeDriver: true}),
    ]));
    cycle.start();
    return () => cycle.stop();
  }, [active, offset, opacity, scrollDistance, text]);
  return <View style={[styles.marqueeClip, {width}]}><Animated.Text numberOfLines={1} style={[styles.marqueeText, {opacity, transform: [{translateX: offset}]}]}>{text}</Animated.Text></View>;
}

/** HOME useSpaceAnimation: immediate 1920px route jump; only arrival fades. */
export function ShellSpaceHost({selectedIndex, children}: {selectedIndex: number; children: React.ReactNode}) {
  const opacity = useRef(new Animated.Value(0)).current;
  const pages = React.Children.toArray(children);
  useEffect(() => {
    opacity.setValue(0);
    const animation = Animated.spring(opacity, {toValue: 1, stiffness: 180, damping: 26, mass: 1, overshootClamping: true, useNativeDriver: true});
    animation.start();
    return () => animation.stop();
  }, [opacity, selectedIndex]);
  return <View style={styles.spaceHost}>
    <Animated.View style={[styles.spacePage, {opacity}]}>{pages[Math.max(0, Math.min(pages.length - 1, selectedIndex))]}</Animated.View>
  </View>;
}

const styles = StyleSheet.create({
  fill: {position: 'absolute', inset: 0, width: '100%', height: '100%'},
  dim: {opacity: 0.7}, disabled: {opacity: 0.4},
  containerFocus: {position: 'absolute', inset: 3, borderWidth: 3, borderRadius: 16, borderColor: '#fff'},
  functionPanel: {position: 'absolute', left: 1188, top: 126, width: 652, minHeight: 216, maxHeight: 810, borderRadius: 16, overflow: 'hidden', backgroundColor: '#080a0f'},
  functionHeader: {height: 80, paddingHorizontal: 24, opacity: 0.7, flexDirection: 'row', alignItems: 'center'},
  functionHeaderIcon: {width: 48, height: 48, marginRight: 16, borderRadius: 8, backgroundColor: 'rgba(255,255,255,0.12)', alignItems: 'center', justifyContent: 'center'},
  functionHeaderText: {flex: 1, color: '#fff', ...shellTextStyle('SizeXSmall')},
  functionList: {flex: 1}, functionItem: {minHeight: 98, flexDirection: 'row', alignItems: 'center', paddingLeft: 24},
  functionItemText: {flex: 1, color: '#fff', ...shellTextStyle('SizeXSmall')}, functionRightIcon: {width: 48, height: 48, marginHorizontal: 16, alignItems: 'center', justifyContent: 'center'}, functionGlyph: {color: '#fff', ...shellTextStyle('SizeXSmall')},
  functionRow: {height: 208, flexDirection: 'row', alignItems: 'flex-start', overflow: 'visible'},
  functionTileSlot: {height: 208, overflow: 'visible'}, functionTileSlotFocused: {width: 168, marginRight: 16}, functionTileSlotResting: {width: 106, marginRight: 8},
  functionTile: {backgroundColor: 'rgba(255,255,255,0.12)', alignItems: 'center', justifyContent: 'center'}, functionTileFocused: {width: 168, height: 168, borderRadius: SHELL_OVERLAY_METRICS.functionRow.focusedRadius}, functionTileResting: {width: 106, height: 106, borderRadius: 16},
  functionTileGlyph: {color: '#fff', ...shellTextStyle('SizeLarge')}, functionTileFocus: {position: 'absolute', inset: -6, borderWidth: 3, borderColor: '#fff', borderRadius: 30}, functionCaption: {position: 'absolute', top: 174, width: 240, color: '#fff', ...shellTextStyle('SizeNormal')},
  hubNavHorizontal: {marginLeft: 148, marginRight: 172, paddingTop: 40, flexDirection: 'row'}, hubNavVertical: {width: 2152, marginTop: 86, marginLeft: 40},
  hubNavHorizontalItem: {height: 72, minWidth: 180, paddingHorizontal: 22, alignItems: 'center', justifyContent: 'center'}, hubNavVerticalItem: {height: 86, width: 388, paddingHorizontal: 22, justifyContent: 'center'}, hubNavText: {color: '#fff', ...shellTextStyle('SizeXSmall')},
  sceneList: {marginTop: -40}, scene: {marginBottom: 48}, sceneHeading: {marginBottom: 16, color: 'rgba(255,255,255,0.8)', ...shellTextStyle('SizeSmall')}, sceneRow: {flexDirection: 'row'},
  sceneTile: {width: 296, height: 344, marginRight: 32, overflow: 'visible', backgroundColor: 'rgba(255,255,255,0.14)'}, sceneFallback: {position: 'absolute', inset: 0, backgroundColor: '#353535'}, sceneTileFocus: {position: 'absolute', left: -6, right: -6, top: -6, height: 308, borderWidth: 3, borderColor: '#fff'}, sceneTileLabel: {position: 'absolute', left: 0, right: 0, top: 312, color: '#fff', ...shellTextStyle('SizeXSmall')},
  hubViewer: {position: 'absolute', inset: 0}, hubHeader: {height: 74, flexDirection: 'row', alignItems: 'center'}, hubIcon: {width: 48, height: 48, marginRight: 16, borderRadius: 12}, hubIconFallback: {width: 48, height: 48, marginRight: 16, borderRadius: 12, backgroundColor: 'rgba(255,255,255,0.14)'}, hubSeparator: {width: 2, height: 36, marginLeft: 12, marginRight: 26, backgroundColor: 'rgba(255,255,255,0.3)'}, hubTag: {color: 'rgba(255,255,255,0.7)', ...shellTextStyle('SizeXSmall')}, hubBody: {flex: 1},
  marqueeClip: {overflow: 'hidden'}, marqueeText: {color: '#fff', ...shellTextStyle('SizeNormal')},
  spaceHost: {position: 'absolute', inset: 0, overflow: 'hidden'}, spacePage: {position: 'absolute', inset: 0},
});
