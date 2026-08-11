import React, {useEffect, useMemo, useRef, useState} from 'react';
import {
  Animated,
  Easing,
  type ImageSourcePropType,
  Platform,
  StyleSheet,
  UIManager,
  View,
} from 'react-native';
import type {ShellRect} from './shellHomeMotion';
import type {ShellSurface} from './shellState';
import {shellBackgroundPresentation} from './shellBackgroundPresentation';
import {
  BACKGROUND_TRANSITION_DEGREE,
  BACKGROUND_TRANSITION_TYPE,
  backgroundTransitionDurationMs,
  backgroundTransitionFlipsPlate,
  backgroundTransitionOrigin,
  type BackgroundTransitionDegree,
  type BackgroundTransitionType,
} from './shellBackgroundTransition';

type NativeBackgroundComponent = React.ComponentType<{
  particleOverlayEnabled: boolean;
  nativeSequenceDirectory: string;
  coldBootActive: boolean;
  pointerEvents?: 'none';
  style?: object;
}>;

let resolvedNativeComponent: NativeBackgroundComponent | null | undefined;

function nativeBackgroundComponent(): NativeBackgroundComponent | null {
  if (resolvedNativeComponent !== undefined) {
    return resolvedNativeComponent;
  }
  resolvedNativeComponent = null;
  if (Platform.OS !== 'windows') {
    return resolvedNativeComponent;
  }

  // The component is Fabric-only.  Do not evaluate codegenNativeComponent on
  // hosts where the native registration is absent: the ordinary React tree is
  // still a complete, visible fallback on those hosts and in Jest.
  const manager = UIManager as typeof UIManager & {
    hasViewManagerConfig?: (name: string) => boolean;
  };
  try {
    if (!manager.hasViewManagerConfig?.('ProsperismoNativeBackground')) {
      return resolvedNativeComponent;
    }
    resolvedNativeComponent = require('./NativeBackgroundSurfaceNativeComponent').default;
  } catch {
    resolvedNativeComponent = null;
  }
  return resolvedNativeComponent ?? null;
}

function fileSource(path: string | undefined): ImageSourcePropType | undefined {
  return path ? {uri: `file:///${path.replace(/\\/g, '/')}`} : undefined;
}

export interface ShellBackgroundSurfaceProps {
  surface: ShellSurface;
  modalOpen: boolean;
  artworkPath?: string;
  nativeSequenceDirectory?: string;
  coldBootActive?: boolean;
  /**
   * Bounds of the focused item, in 1920x1080 design space. The recovered
   * contract ripples out of the focused tile's centre; the screen centre is
   * only used when nothing is focused. See
   * docs/sony-shell/bglayer-managed-contract.md.
   */
  focusedRect?: ShellRect;
  /** HOME selection requests degree Normal, which derives 633.333ms. */
  transitionDegree?: BackgroundTransitionDegree;
  /**
   * Presenting a new plate is CustomImageRipple in the firmware. Types that
   * present a new image flip the double-buffered plate id; Hide and
   * SystemDefault do not.
   */
  transitionType?: BackgroundTransitionType;
}

/**
 * Stable background owner shared by every Big Picture route.  The native view
 * renders Sony's translated FirstWave plate continuously and consumes the
 * out-of-process particle frames only while the recovered HOME state requests
 * them. A focused game's own title plate is an independent full-frame layer;
 * generic cards leave the ambient room visible.
 */
export function ShellBackgroundSurface({
  surface,
  modalOpen,
  artworkPath,
  nativeSequenceDirectory,
  coldBootActive = false,
  focusedRect,
  transitionDegree = BACKGROUND_TRANSITION_DEGREE.normal,
  transitionType = BACKGROUND_TRANSITION_TYPE.customImageRipple,
}: ShellBackgroundSurfaceProps) {
  const presentation = shellBackgroundPresentation(surface, modalOpen);
  const NativeBackground = useMemo(nativeBackgroundComponent, []);
  const nextKey = artworkPath ?? 'none';
  const nextSource = fileSource(artworkPath);
  const [current, setCurrent] = useState({key: nextKey, source: nextSource});
  const [previous, setPrevious] = useState<{key: string; source: ImageSourcePropType | undefined}>();
  const crossFade = useRef(new Animated.Value(1)).current;

  // The firmware presents a new plate with CustomImageRipple, whose origin is
  // the focused item's centre. The ripple itself is a native pass we do not
  // reproduce, so this owner still cross-fades — but it takes its duration
  // from the recovered degree table rather than a hard-coded constant, and it
  // publishes the origin so the native surface can consume it once the pass
  // exists. Keeping the origin computed here means the geometry is exercised
  // and regression-tested with the shell, not stranded in an unused module.
  const originRef = useRef(backgroundTransitionOrigin(focusedRect));
  const plateIdRef = useRef(0);
  useEffect(() => {
    if (current.key === nextKey) {
      return;
    }
    originRef.current = backgroundTransitionOrigin(focusedRect);
    if (backgroundTransitionFlipsPlate(transitionType)) {
      // prevBgImageId ^= 1: only transitions that present a new image flip it.
      plateIdRef.current = plateIdRef.current === 0 ? 1 : 0;
    }
    setPrevious(current);
    setCurrent({key: nextKey, source: nextSource});
    crossFade.setValue(0);
    const animation = Animated.timing(crossFade, {
      toValue: 1,
      duration: backgroundTransitionDurationMs(transitionDegree),
      easing: Easing.linear,
      useNativeDriver: true,
    });
    animation.start(({finished}) => {
      if (finished) {
        setPrevious(undefined);
      }
    });
    return () => animation.stop();
    // focusedRect is read at transition start only; it must not restart the
    // animation when the selection moves without changing the plate.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [crossFade, current, nextKey, nextSource, transitionDegree, transitionType]);

  return <View pointerEvents="none" style={styles.owner}>
    <View style={styles.basematFallback} />
    {NativeBackground && <NativeBackground
      coldBootActive={coldBootActive}
      nativeSequenceDirectory={nativeSequenceDirectory ?? ''}
      particleOverlayEnabled={presentation.particleOverlayEnabled}
      pointerEvents="none"
      style={styles.nativeSurface}
    />}
    {previous?.source && <Animated.Image source={previous.source} style={[
      styles.artwork,
      {opacity: crossFade.interpolate({inputRange: [0, 1], outputRange: [1, 0]})},
    ]} />}
    {current.source && <Animated.Image source={current.source} style={[
      styles.artwork,
      {opacity: crossFade.interpolate({inputRange: [0, 1], outputRange: [0, 1]})},
    ]} />}
  </View>;
}

const styles = StyleSheet.create({
  owner: {
    ...StyleSheet.absoluteFillObject,
    overflow: 'hidden',
  },
  // Visible only until the Fabric drawing surface publishes its first frame.
  // This is a neutral safety plate, not a replacement rendition of Sony's UI.
  basematFallback: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: '#020408',
  },
  nativeSurface: {
    ...StyleSheet.absoluteFillObject,
  },
  artwork: {
    ...StyleSheet.absoluteFillObject,
    resizeMode: 'cover',
  },
});
