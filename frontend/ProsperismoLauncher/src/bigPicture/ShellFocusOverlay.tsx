import React from 'react';
import {StyleSheet, View} from 'react-native';
import {shellFocusRingComponent} from './nativeShellComponents';
import {useShellFocusNoisePath} from './ShellFocusNoise';

// Resolved once; null off Windows, where the codegen module is never required.
const ProsperismoFocusRing = shellFocusRingComponent();

export interface ShellFocusOverlayProps {
  active: boolean;
  width: number;
  height: number;
  radius: number;
  /** FocusStyle.ListItem crops only the source body, before UI3 inflates it. */
  crop?: {left?: number; top?: number; right?: number; bottom?: number};
}

const SURFACE_MARGIN = 18;

/**
 * One UI3 distance-field focus treatment for every non-Home shell surface.
 * This deliberately replaces route-local stroked boxes: the Home renderer and
 * modal/list renderers now share the recovered 200ms show delay, 250ms warp,
 * 300ms moving driver, exterior 3px offset, and radius inheritance.
 */
export function ShellFocusOverlay({active, width, height, radius, crop}: ShellFocusOverlayProps) {
  const noisePath = useShellFocusNoisePath();
  const left = crop?.left ?? 0;
  const top = crop?.top ?? 0;
  const bodyWidth = Math.max(1, width - left - (crop?.right ?? 0));
  const bodyHeight = Math.max(1, height - top - (crop?.bottom ?? 0));
  const surfaceWidth = width + SURFACE_MARGIN * 2;
  const surfaceHeight = height + SURFACE_MARGIN * 2;
  if (!ProsperismoFocusRing) {
    // Outline stand-in; not the recovered UI3 treatment. See RecoveredHomeShell.
    return active ? <View
      pointerEvents="none"
      style={[
        styles.surface,
        styles.fallback,
        {left: left - 3, top: top - 3, width: bodyWidth + 6, height: bodyHeight + 6,
         borderRadius: radius + 3},
      ]}
    /> : null;
  }
  return <ProsperismoFocusRing
    active={active}
    keyRepeating={false}
    noisePath={noisePath}
    offsetX={0}
    offsetY={0}
    pointerEvents="none"
    pressedToken={0}
    radius={radius}
    screenHeight={1080}
    screenWidth={1920}
    style={[
      styles.surface,
      {
        left: -SURFACE_MARGIN,
        top: -SURFACE_MARGIN,
        width: surfaceWidth,
        height: surfaceHeight,
      },
    ]}
    surfaceHeight={surfaceHeight}
    surfaceWidth={surfaceWidth}
    targetHeight={bodyHeight}
    targetWidth={bodyWidth}
    targetX={SURFACE_MARGIN + left}
    targetY={SURFACE_MARGIN + top}
  />;
}

const styles = StyleSheet.create({
  surface: {position: 'absolute', zIndex: 2},
  fallback: {borderWidth: 3, borderColor: '#FFFFFF'},
});
