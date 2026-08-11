import React, {useEffect, useRef, useState} from 'react';
import {
  Animated,
  type ImageSourcePropType,
  Pressable,
  StyleSheet,
  Text,
  type ViewStyle,
  View,
} from 'react-native';
import type {GameInstall} from '../core/models';
import {
  HomeGlanceState,
  HOME_GEOMETRY,
  HOME_SPRINGS,
  HomeStartupChoreography,
  homeTileLeft,
  homeTileMatOpacity,
  homeTileRadius,
  homeTitleNameWidth,
  ShellSpring,
  systemIconFocusBackgroundOpacity,
  systemIconFocusedChannel,
  type ShellRect,
} from './shellHomeMotion';
import {SHELL_CLOCK_TEXT_STYLE, SHELL_FONT_FAMILY, SHELL_FONT_SIZE_PS} from './shellTypography';
import {HUB_TRANSITION, hubSelectedTileRect, hubTitlePose} from './shellHubTransition';
import type {ShellVerticalPosition} from './shellState';
import {shellFocusRingComponent, shellLocalImageComponent} from './nativeShellComponents';
import {useShellFocusNoisePath} from './ShellFocusNoise';
import type {ShellFocusRegion, ShellSpace} from './shellState';

// Resolved once. On a non-Windows host these return null without ever
// requiring the codegen modules, which is what makes the shell renderable
// off-Windows at all.
const ProsperismoFocusRing = shellFocusRingComponent();
const ProsperismoLocalImage = shellLocalImageComponent();

const DESIGN_WIDTH = HOME_GEOMETRY.designWidth;
const DESIGN_HEIGHT = HOME_GEOMETRY.designHeight;
const SYSTEM_HEIGHT = HOME_GEOMETRY.systemHeight;
const CONTENT_INSET = HOME_GEOMETRY.contentInset;
const SELECTED_TILE_LEFT = HOME_GEOMETRY.focusedTileLeft;
const SELECTED_TILE_SIZE = HOME_GEOMETRY.focusedTileSize;
const IDLE_TILE_SIZE = HOME_GEOMETRY.tileSize;
const IDLE_TILE_TOP = SYSTEM_HEIGHT + (SELECTED_TILE_SIZE - IDLE_TILE_SIZE) / 2;
const SELECTED_TILE_RADIUS = homeTileRadius(SELECTED_TILE_SIZE);
const TITLE_LEFT = HOME_GEOMETRY.titleX;
const TITLE_TOP = SYSTEM_HEIGHT + IDLE_TILE_SIZE;
const SYSTEM_ICON_SIZE = HOME_GEOMETRY.systemIconSize;
const SYSTEM_ICON_PITCH = HOME_GEOMETRY.systemIconSize + HOME_GEOMETRY.systemIconMargin;
const SYSTEM_ICON_COUNT = 3;
const CLOCK_MARGIN_LEFT = 88;
const CLOCK_WIDTH = 120;
const SYSTEM_GROUP_WIDTH =
  SYSTEM_ICON_COUNT * SYSTEM_ICON_SIZE
  + (SYSTEM_ICON_COUNT - 1) * (SYSTEM_ICON_PITCH - SYSTEM_ICON_SIZE)
  + CLOCK_MARGIN_LEFT
  + CLOCK_WIDTH;
const SYSTEM_GROUP_LEFT = DESIGN_WIDTH - CONTENT_INSET - SYSTEM_GROUP_WIDTH;
interface TileMotion {
  left: ShellSpring;
  side: ShellSpring;
  released: boolean;
}

type SystemAction = 'search' | 'settings' | 'profile';

const SYSTEM_ACTIONS: readonly {label: string; action: SystemAction}[] = [
  {label: 'Search', action: 'search'},
  {label: 'Settings', action: 'settings'},
  {label: 'Profile', action: 'profile'},
];

export interface RecoveredHomeShellProps {
  games: readonly GameInstall[];
  focusRegion: ShellFocusRegion;
  verticalPosition: ShellVerticalPosition;
  selectedIndex: number;
  selectedSpace: ShellSpace;
  focusedSpace: ShellSpace;
  selectedSystemIndex: number;
  clock: string;
  backgroundPath?: string;
  settingsIconPath?: string;
  searchIconPath?: string;
  libraryIconPath?: string;
  genericGameIconPath?: string;
  viewportWidth: number;
  viewportHeight: number;
  onSelectGame(index: number): void;
  onLaunch(game: GameInstall): void;
  onOptions(game: GameInstall): void;
  onFocusSpace(space: ShellSpace): void;
  onActivateSpace(space: ShellSpace): void;
  onSelectSystem(index: number): void;
  onActivateSystem(action: SystemAction): void;
  onOpenLibrary(): void;
  onFocusLibrary(): void;
  libraryRef: React.MutableRefObject<any>;
  spaceRefs: React.MutableRefObject<any[]>;
  strandRefs: React.MutableRefObject<any[]>;
  systemRefs: React.MutableRefObject<any[]>;
}

const localImageSources = new Map<string, ImageSourcePropType>();

function fileSource(path: string | undefined): ImageSourcePropType | undefined {
  if (!path) {
    return undefined;
  }
  const cached = localImageSources.get(path);
  if (cached) {
    return cached;
  }
  const source = {uri: `file:///${path.replace(/\\/g, '/')}`};
  localImageSources.set(path, source);
  return source;
}

function SearchGlyph({color}: {color: string}) {
  return <View style={glyphStyles.search}>
    <View style={[glyphStyles.searchLens, {borderColor: color}]} />
    <View style={[glyphStyles.searchHandle, {backgroundColor: color}]} />
  </View>;
}

function SettingsGlyph({color}: {color: string}) {
  return <View style={[glyphStyles.settingsOuter, {borderColor: color}]}>
    <View style={[glyphStyles.settingsInner, {borderColor: color}]} />
  </View>;
}

function ProfileGlyph({color}: {color: string}) {
  return <View style={[glyphStyles.profile, {borderColor: color}]}>
    <View style={[glyphStyles.profileHead, {backgroundColor: color}]} />
    <View style={[glyphStyles.profileShoulders, {borderColor: color}]} />
  </View>;
}

function SystemGlyph({action, tint, tintChannel, settingsIconPath, searchIconPath}: {
  action: SystemAction;
  tint: string;
  tintChannel: number;
  settingsIconPath?: string;
  searchIconPath?: string;
}) {
  if (ProsperismoLocalImage && action === 'settings' && settingsIconPath) {
    return <ProsperismoLocalImage
      contain
      displayHeight={48}
      displayWidth={48}
      path={settingsIconPath}
      style={glyphStyles.firmwareIcon}
      tintBlue={tintChannel}
      tintGreen={tintChannel}
      tintRed={tintChannel}
    />;
  }
  if (ProsperismoLocalImage && action === 'search' && searchIconPath) {
    return <ProsperismoLocalImage
      contain
      displayHeight={48}
      displayWidth={48}
      path={searchIconPath}
      style={glyphStyles.firmwareIcon}
      tintBlue={tintChannel}
      tintGreen={tintChannel}
      tintRed={tintChannel}
    />;
  }
  if (action === 'search') {
    return <SearchGlyph color={tint} />;
  }
  if (action === 'settings') {
    return <SettingsGlyph color={tint} />;
  }
  return <ProfileGlyph color={tint} />;
}

function homeFocusTarget(
  region: ShellFocusRegion,
  selectedSpace: ShellSpace,
  systemIndex: number,
  hasGames: boolean,
  measuredSpaceRect?: ShellRect,
): {rect: ShellRect; radius: number} | undefined {
  if (region === 'strand' && hasGames) {
    return {
      rect: {x: SELECTED_TILE_LEFT, y: SYSTEM_HEIGHT, width: SELECTED_TILE_SIZE, height: SELECTED_TILE_SIZE},
      radius: SELECTED_TILE_RADIUS,
    };
  }
  if (region === 'library-shortcut') {
    return {
      rect: {x: 1602, y: IDLE_TILE_TOP, width: IDLE_TILE_SIZE, height: IDLE_TILE_SIZE},
      radius: HOME_GEOMETRY.tileRadius,
    };
  }
  if (region === 'system') {
    const index = Math.max(0, Math.min(systemIndex, SYSTEM_ICON_COUNT - 1));
    return {
      rect: {
        x: SYSTEM_GROUP_LEFT + index * SYSTEM_ICON_PITCH,
        y: (SYSTEM_HEIGHT - SYSTEM_ICON_SIZE) / 2,
        width: SYSTEM_ICON_SIZE,
        height: SYSTEM_ICON_SIZE,
      },
      radius: SYSTEM_ICON_SIZE / 2,
    };
  }
  if (region === 'spaces') {
    // HOME's space switcher has its own named focus region: the label
    // container (padding included), not a second card. The live rect is
    // measured from layout; the authored bounds remain as a pre-layout
    // fallback.
    if (measuredSpaceRect) {
      return {rect: measuredSpaceRect, radius: 8};
    }
    const media = selectedSpace === 'media';
    return {
      rect: {x: media ? 235 : 84, y: 35, width: media ? 96 : 104, height: 56},
      radius: 8,
    };
  }
  return undefined;
}

function tileFrameStyle(
  scale: number,
  left: number,
  top: number,
  side: number,
  isSelected: boolean,
  distance: number,
  opacityScale = 1,
): ViewStyle {
  return {
    left: left * scale,
    top: top * scale,
    width: side * scale,
    height: side * scale,
    borderRadius: homeTileRadius(side) * scale,
    opacity: side > 0.5 ? opacityScale : 0,
    zIndex: isSelected ? 1000 : 500 - distance,
  };
}

function tileMatStyle(opacity: number): ViewStyle {
  return {backgroundColor: `rgba(2,4,8,${opacity})`};
}

function systemGlanceStyle(scale: number): ViewStyle {
  return {transform: [{scale}]};
}

function systemLabelStyle(opacity: number): ViewStyle {
  return {opacity};
}

/**
 * A deliberately small port of the recovered SharpEmu HOME surface. Every
 * coordinate is authored in the firmware bundle's 1920 x 1080 design space,
 * then multiplied into the available viewport. No native child surface is
 * mounted here, so the React tree remains visible while the background bridge
 * is repaired independently.
 */
export function RecoveredHomeShell({
  games,
  focusRegion,
  verticalPosition,
  selectedIndex,
  selectedSpace,
  focusedSpace,
  selectedSystemIndex,
  clock,
  backgroundPath,
  settingsIconPath,
  searchIconPath,
  libraryIconPath,
  genericGameIconPath,
  viewportWidth,
  viewportHeight,
  onSelectGame,
  onLaunch,
  onOptions,
  onFocusSpace,
  onActivateSpace,
  onSelectSystem,
  onActivateSystem,
  onOpenLibrary,
  onFocusLibrary,
  libraryRef,
  spaceRefs,
  strandRefs,
  systemRefs,
}: RecoveredHomeShellProps) {
  const visibleGames = games.slice(0, HOME_GEOMETRY.maxTiles);
  const clampedIndex = Math.max(0, Math.min(selectedIndex, Math.max(0, visibleGames.length - 1)));
  const selectedGame = visibleGames[clampedIndex];
  const startup = useRef(new HomeStartupChoreography());
  const tileMotion = useRef<TileMotion[]>([]);
  const systemGlance = useRef(Array.from({length: SYSTEM_ICON_COUNT}, () => new HomeGlanceState()));
  const spaceLayouts = useRef<(ShellRect | undefined)[]>([]);
  // m130 vertical axis (SPRING_OPTIONS_FAST) plus the m571 FASTER fade of
  // non-selected tiles. Both rest at 0 while the focusReady gate stays closed.
  const verticalMotion = useRef(new ShellSpring());
  const strandFade = useRef(new ShellSpring());
  const revealElapsedMs = useRef(0);
  const [, setMotionFrame] = useState(0);
  const [focusPressToken, setFocusPressToken] = useState(0);
  const focusNoisePath = useShellFocusNoisePath();

  if (tileMotion.current.length !== visibleGames.length) {
    tileMotion.current = visibleGames.map((_, index) => {
      const left = new ShellSpring();
      const side = new ShellSpring();
      left.snapTo(homeTileLeft(index, clampedIndex));
      side.snapTo(0);
      return {left, side, released: false};
    });
    revealElapsedMs.current = 0;
    startup.current.begin(visibleGames.length);
  }

  useEffect(() => {
    tileMotion.current.forEach((motion, index) => {
      motion.left.springTo(homeTileLeft(index, clampedIndex), HOME_SPRINGS.strand);
      if (motion.released) {
        motion.side.springTo(index === clampedIndex ? SELECTED_TILE_SIZE : IDLE_TILE_SIZE, HOME_SPRINGS.strand);
      }
    });
  }, [clampedIndex, visibleGames.length]);

  useEffect(() => {
    systemGlance.current.forEach((glance, index) =>
      glance.setGlanced(focusRegion === 'system' && selectedSystemIndex === index),
    );
  }, [focusRegion, selectedSystemIndex]);

  useEffect(() => {
    const target = verticalPosition === 'hub' ? 1 : 0;
    verticalMotion.current.springTo(target, HOME_SPRINGS.fast);
    strandFade.current.springTo(target, HOME_SPRINGS.faster);
  }, [verticalPosition]);

  useEffect(() => {
    let active = true;
    let frameHandle = 0;
    let previous = Date.now();
    const tick = () => {
      if (!active) {
        return;
      }
      const now = Date.now();
      const deltaMs = Math.max(0, Math.min(now - previous, 64));
      previous = now;
      revealElapsedMs.current += deltaMs;
      startup.current.advance(deltaMs);
      tileMotion.current.forEach((motion, index) => {
        if (!motion.released && revealElapsedMs.current >= index * HomeStartupChoreography.tileStaggerMs) {
          motion.released = true;
          motion.side.springTo(index === clampedIndex ? SELECTED_TILE_SIZE : IDLE_TILE_SIZE, HOME_SPRINGS.slower);
        }
        motion.left.advance(deltaMs / 1000);
        motion.side.advance(deltaMs / 1000);
      });
      systemGlance.current.forEach(glance => glance.advance(deltaMs / 1000));
      verticalMotion.current.advance(deltaMs / 1000);
      strandFade.current.advance(deltaMs / 1000);
      setMotionFrame(value => value + 1);
      frameHandle = requestAnimationFrame(tick);
    };
    frameHandle = requestAnimationFrame(tick);
    return () => {
      active = false;
      cancelAnimationFrame(frameHandle);
    };
  }, [clampedIndex]);

  const scale = Math.max(0.01, Math.min(viewportWidth / DESIGN_WIDTH, viewportHeight / DESIGN_HEIGHT));
  const s = (value: number) => value * scale;
  const stageWidth = s(DESIGN_WIDTH);
  const stageHeight = s(DESIGN_HEIGHT);
  const stageStyles = StyleSheet.create({
    stage: {width: stageWidth, height: stageHeight, backgroundColor: 'transparent', overflow: 'hidden'},
    background: {position: 'absolute', left: 0, top: 0, width: stageWidth, height: stageHeight, resizeMode: 'cover', opacity: 0.18},
    topBand: {position: 'absolute', left: 0, top: 0, width: stageWidth, height: s(SYSTEM_HEIGHT)},
    spaces: {position: 'absolute', left: s(CONTENT_INSET), top: 0, height: s(SYSTEM_HEIGHT), flexDirection: 'row', alignItems: 'center'},
    spaceButton: {padding: s(8), marginRight: s(64)},
    spaceText: {fontFamily: SHELL_FONT_FAMILY, fontSize: s(SHELL_FONT_SIZE_PS.SizeLarge), color: 'rgba(255,255,255,0.6)'},
    spaceTextSelected: {color: '#FFFFFF', fontWeight: '700'},
    systemButton: {position: 'absolute', top: s(35), width: s(SYSTEM_ICON_SIZE), height: s(SYSTEM_ICON_SIZE), borderRadius: s(28), alignItems: 'center', justifyContent: 'center'},
    systemGlyphHost: {width: s(56), height: s(56), alignItems: 'center', justifyContent: 'center'},
    systemLabel: {position: 'absolute', left: s(-156), top: s(60), width: s(368), color: '#FFFFFF', fontFamily: SHELL_FONT_FAMILY, fontSize: s(SHELL_FONT_SIZE_PS.SizeXSmall), fontWeight: '400', textAlign: 'center'},
    clockWrap: {position: 'absolute', left: s(SYSTEM_GROUP_LEFT + SYSTEM_ICON_COUNT * SYSTEM_ICON_PITCH - (SYSTEM_ICON_PITCH - SYSTEM_ICON_SIZE) + CLOCK_MARGIN_LEFT), top: 0, height: s(SYSTEM_HEIGHT), width: s(CLOCK_WIDTH), justifyContent: 'center'},
    clock: {...SHELL_CLOCK_TEXT_STYLE, fontSize: s(SHELL_FONT_SIZE_PS.SizeLarge), color: '#FFFFFF'},
    tile: {position: 'absolute', backgroundColor: '#292929'},
    tileArtworkPlane: {position: 'absolute'},
    tileFallback: {width: '100%', height: '100%', alignItems: 'center', justifyContent: 'center', backgroundColor: '#353535'},
    selectedMonogram: {fontFamily: SHELL_FONT_FAMILY, fontSize: s(58), fontWeight: '700', color: '#FFFFFF'},
    idleMonogram: {fontFamily: SHELL_FONT_FAMILY, fontSize: s(40), fontWeight: '700', color: '#FFFFFF'},
    focusNative: {position: 'absolute', left: 0, top: 0, width: stageWidth, height: stageHeight, zIndex: 2000},
    focusFallback: {position: 'absolute', zIndex: 2000, borderWidth: s(3), borderColor: '#FFFFFF'},
    hubFrame: {position: 'absolute', left: 0, top: s(HUB_TRANSITION.moduleBoundaryY), width: stageWidth, height: s(DESIGN_HEIGHT - HUB_TRANSITION.moduleBoundaryY)},
    tileMat: {position: 'absolute', left: 0, top: 0, right: 0, bottom: 0},
    titleBlock: {position: 'absolute', left: s(TITLE_LEFT), top: s(TITLE_TOP), width: s(homeTitleNameWidth({})), height: s(62), justifyContent: 'center'},
    title: {fontFamily: SHELL_FONT_FAMILY, color: '#FFFFFF', fontSize: s(SHELL_FONT_SIZE_PS.SizeNormal), fontWeight: '400'},
    library: {position: 'absolute', left: s(1602), top: s(IDLE_TILE_TOP), width: s(IDLE_TILE_SIZE), height: s(IDLE_TILE_SIZE), borderRadius: s(16), alignItems: 'center', justifyContent: 'center', backgroundColor: '#353535'},
    libraryIcon: {width: s(40), height: s(32)},
    libraryFallback: {fontFamily: SHELL_FONT_FAMILY, color: '#FFFFFF', fontSize: s(34)},
    emptyTitle: {position: 'absolute', left: s(172), top: s(168), color: '#FFFFFF', fontFamily: SHELL_FONT_FAMILY, fontSize: s(26), fontWeight: '600'},
    emptyHint: {position: 'absolute', left: s(172), top: s(208), color: 'rgba(255,255,255,0.7)', fontFamily: SHELL_FONT_FAMILY, fontSize: s(18)},
  });

  const background = fileSource(backgroundPath);
  const entrance = startup.current;
  // Hub vertical progress. hubRect already folds in the -166v home lift, so
  // its deltas move only the selected tile; every other home element takes
  // the raw lift.
  const vertical = verticalMotion.current.value;
  const lift = HUB_TRANSITION.homeLift * vertical;
  const hubRect = hubSelectedTileRect(vertical);
  const hubDX = hubRect.x - SELECTED_TILE_LEFT;
  const hubDY = hubRect.y - SYSTEM_HEIGHT;
  const hubDS = hubRect.width - SELECTED_TILE_SIZE;
  const titlePose = hubTitlePose(vertical);
  const idleFade = 1 - strandFade.current.value;
  const focusTarget = homeFocusTarget(
    focusRegion,
    focusedSpace,
    selectedSystemIndex,
    visibleGames.length > 0,
    focusRegion === 'spaces'
      ? spaceLayouts.current[focusedSpace === 'media' ? 1 : 0]
      : undefined,
  );
  const focusOffset =
    focusRegion === 'strand' || focusRegion === 'library-shortcut'
      ? {x: entrance.switcherTranslateX, y: entrance.switcherTranslateY + lift}
      : {x: 0, y: 0};

  return <View style={stageStyles.stage}>
    {background && <Animated.Image source={background} style={stageStyles.background} />}

    <View style={[stageStyles.topBand, {top: s(entrance.systemTranslateY + lift), opacity: entrance.systemAlpha}]}>
      <View style={stageStyles.spaces}>{(['games', 'media'] as const).map((space, index) => {
        const selected = selectedSpace === space;
        return <Pressable
          accessibilityLabel={space === 'games' ? 'Games' : 'Media'}
          accessibilityRole="button"
          key={space}
          onFocus={() => onFocusSpace(space)}
          onLayout={event => {
            const layout = event.nativeEvent.layout;
            spaceLayouts.current[index] = {
              x: CONTENT_INSET + layout.x / scale,
              y: layout.y / scale,
              width: layout.width / scale,
              height: layout.height / scale,
            };
          }}
          onPressIn={() => setFocusPressToken(value => value + 1)}
          onPress={() => onActivateSpace(space)}
          ref={node => { spaceRefs.current[index] = node; }}
          style={stageStyles.spaceButton}>
          <Text style={[stageStyles.spaceText, selected && stageStyles.spaceTextSelected]}>
            {space === 'games' ? 'Games' : 'Media'}
          </Text>
        </Pressable>;
      })}</View>

      {SYSTEM_ACTIONS.map((item, index) => {
        const glance = systemGlance.current[index];
        const backgroundOpacity = systemIconFocusBackgroundOpacity(glance.iconScale);
        const tintChannel = systemIconFocusedChannel(glance.iconScale);
        const tint = `rgb(${tintChannel},${tintChannel},${tintChannel})`;
        return <Pressable
          accessibilityLabel={item.label}
          accessibilityRole="button"
          key={item.action}
          onFocus={() => onSelectSystem(index)}
          onPressIn={() => setFocusPressToken(value => value + 1)}
          onPress={() => onActivateSystem(item.action)}
          ref={node => { systemRefs.current[index] = node; }}
          style={[
            stageStyles.systemButton,
            {left: s(SYSTEM_GROUP_LEFT + index * SYSTEM_ICON_PITCH)},
            {backgroundColor: `rgba(255,255,255,${backgroundOpacity})`},
          ]}>
          <View style={[stageStyles.systemGlyphHost, systemGlanceStyle(glance.iconScale)]}>
            <SystemGlyph
              action={item.action}
              tint={tint}
              tintChannel={tintChannel}
              settingsIconPath={settingsIconPath}
              searchIconPath={searchIconPath}
            />
          </View>
          <Text numberOfLines={1} style={[stageStyles.systemLabel, systemLabelStyle(glance.labelOpacity)]}>{item.label}</Text>
        </Pressable>;
      })}
      <View style={stageStyles.clockWrap}>
        <Text style={stageStyles.clock}>{clock}</Text>
      </View>
    </View>

    {selectedGame ? <>
      {visibleGames.map((game, index) => {
        const isSelected = index === clampedIndex;
        const motion = tileMotion.current[index];
        const baseSide = Math.max(0, motion?.side.value ?? (isSelected ? SELECTED_TILE_SIZE : IDLE_TILE_SIZE));
        const side = isSelected ? Math.max(0, baseSide + hubDS) : baseSide;
        const left = (motion?.left.value ?? homeTileLeft(index, clampedIndex))
          + entrance.switcherTranslateX
          + (isSelected ? hubDX : 0);
        const top = SYSTEM_HEIGHT + (SELECTED_TILE_SIZE - baseSide) / 2 + entrance.switcherTranslateY
          + (isSelected ? hubDY : lift);
        const tileFade = isSelected ? 1 : idleFade;
        const matOpacity = homeTileMatOpacity(index, clampedIndex);
        const distance = Math.abs(index - clampedIndex);
        const frame = tileFrameStyle(scale, left, top, side, isSelected, distance, tileFade);
        const tileSource = game.artworkPath ?? genericGameIconPath;
        const artworkFrame = {
          left: left * scale,
          top: top * scale,
          width: side * scale,
          height: side * scale,
          borderRadius: homeTileRadius(side) * scale,
          opacity: side > 0.5 ? tileFade : 0,
          zIndex: isSelected ? 999 : 499 - distance,
        };
        return <React.Fragment key={game.gamePath}>
          {tileSource && (ProsperismoLocalImage
            ? <ProsperismoLocalImage
                contain={false}
                displayHeight={side * scale}
                displayWidth={side * scale}
                path={tileSource}
                style={[
                  stageStyles.tileArtworkPlane,
                  artworkFrame,
                ]}
                tintBlue={255}
                tintGreen={255}
                tintRed={255}
              />
            // Without the WIC renderer, fall back to RN's own file loader.
            : <Animated.Image
                source={fileSource(tileSource) as ImageSourcePropType}
                style={[stageStyles.tileArtworkPlane, artworkFrame]}
              />)}
          <Pressable
            accessibilityLabel={`${game.titleName}, ${index + 1} of ${visibleGames.length}`}
            accessibilityRole="button"
            onFocus={() => onSelectGame(index)}
            onLongPress={() => onOptions(game)}
            onPressIn={() => setFocusPressToken(value => value + 1)}
            onPress={() => onLaunch(game)}
            ref={node => { strandRefs.current[index] = node; }}
            style={[
              stageStyles.tile,
              frame,
              tileSource ? {backgroundColor: 'transparent'} : undefined,
            ]}>
            {!tileSource && <View style={stageStyles.tileFallback}>
              <Text style={isSelected ? stageStyles.selectedMonogram : stageStyles.idleMonogram}>
                {game.titleName.slice(0, 1).toUpperCase()}
              </Text>
            </View>}
            {matOpacity > 0 && <View
              pointerEvents="none"
              style={[stageStyles.tileMat, tileMatStyle(matOpacity)]}
            />}
          </Pressable>
        </React.Fragment>;
      })}
      <View style={[
        stageStyles.titleBlock,
        {
          left: s(titlePose.x + entrance.switcherTranslateX),
          top: s(titlePose.y + entrance.switcherTranslateY),
          opacity: entrance.titleAlpha * (focusRegion === 'strand' ? 1 : 0.7),
        },
      ]}>
        <Text numberOfLines={1} style={stageStyles.title}>{selectedGame.titleName}</Text>
      </View>
    </> : <>
      <Text style={stageStyles.emptyTitle}>Your library is empty</Text>
      <Text style={stageStyles.emptyHint}>Add a game folder from Desktop mode, then return to Big Picture.</Text>
    </>}

    <Pressable
      accessibilityLabel="Game Library"
      accessibilityRole="button"
      onFocus={onFocusLibrary}
      onPressIn={() => setFocusPressToken(value => value + 1)}
      onPress={onOpenLibrary}
      ref={node => { libraryRef.current = node; }}
      style={[
        stageStyles.library,
        {
          left: s(1602 + entrance.switcherTranslateX),
          top: s(IDLE_TILE_TOP + entrance.switcherTranslateY + lift),
          opacity: idleFade,
        },
      ]}>
      {ProsperismoLocalImage && libraryIconPath
        ? <ProsperismoLocalImage
            contain
            displayHeight={s(32)}
            displayWidth={s(40)}
            path={libraryIconPath}
            style={stageStyles.libraryIcon}
            tintBlue={255}
            tintGreen={255}
            tintRed={255}
          />
        : <Text style={stageStyles.libraryFallback}>▦</Text>}
    </Pressable>

    {vertical > 0.001 && <View
      // The guest app module owns everything below the y=128 boundary. The
      // frame renders no content until a genuine hub adapter provides it.
      pointerEvents="none"
      style={stageStyles.hubFrame}
    />}

    {ProsperismoFocusRing
      ? <ProsperismoFocusRing
          active={Boolean(focusTarget)}
          keyRepeating={false}
          noisePath={focusNoisePath}
          offsetX={focusOffset.x}
          offsetY={focusOffset.y}
          pointerEvents="none"
          pressedToken={focusPressToken}
          radius={focusTarget?.radius ?? 0}
          screenHeight={DESIGN_HEIGHT}
          screenWidth={DESIGN_WIDTH}
          style={stageStyles.focusNative}
          surfaceHeight={DESIGN_HEIGHT}
          surfaceWidth={DESIGN_WIDTH}
          targetHeight={focusTarget?.rect.height ?? 0}
          targetWidth={focusTarget?.rect.width ?? 0}
          targetX={focusTarget?.rect.x ?? 0}
          targetY={focusTarget?.rect.y ?? 0}
        />
      // Plain outline stand-in so focus stays legible without the native
      // pass. It is NOT the recovered UI3 highlight - no wash, band, warp or
      // shimmer - and must never be mistaken for it in a fidelity capture.
      : focusTarget && <View
          pointerEvents="none"
          style={[
            stageStyles.focusFallback,
            {
              left: s(focusTarget.rect.x + focusOffset.x - 3),
              top: s(focusTarget.rect.y + focusOffset.y - 3),
              width: s(focusTarget.rect.width + 6),
              height: s(focusTarget.rect.height + 6),
              borderRadius: s(focusTarget.radius + 3),
            },
          ]}
        />}
  </View>;
}

const glyphStyles = StyleSheet.create({
  firmwareIcon: {width: 48, height: 48},
  search: {width: 34, height: 34},
  searchLens: {position: 'absolute', left: 3, top: 3, width: 20, height: 20, borderWidth: 4, borderRadius: 10},
  searchHandle: {position: 'absolute', left: 22, top: 23, width: 13, height: 4, borderRadius: 2, transform: [{rotate: '47deg'}]},
  settingsOuter: {width: 31, height: 31, borderWidth: 4, borderRadius: 8, alignItems: 'center', justifyContent: 'center', transform: [{rotate: '45deg'}]},
  settingsInner: {width: 11, height: 11, borderWidth: 3, borderRadius: 6},
  profile: {width: 42, height: 42, borderWidth: 2, borderRadius: 21, alignItems: 'center'},
  profileHead: {width: 10, height: 10, borderRadius: 5, marginTop: 8},
  profileShoulders: {width: 24, height: 12, borderWidth: 2, borderBottomWidth: 0, borderTopLeftRadius: 12, borderTopRightRadius: 12, marginTop: 4},
});
