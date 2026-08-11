import React from 'react';
import {Platform, UIManager} from 'react-native';

/**
 * Guarded resolution for the shell's Fabric components.
 *
 * `codegenNativeComponent` must not be evaluated on a host where the native
 * registration is absent, and the modules that call it therefore have to be
 * imported lazily rather than statically. ShellBackgroundSurface already did
 * this for its own surface; FocusRing and LocalImage did not, so importing the
 * home shell evaluated them unconditionally and made the tree unrenderable
 * anywhere but Windows.
 *
 * Every one of these has a JavaScript fallback at the call site, which is the
 * design the shell documents: a missing native leaves the ordinary React tree
 * visible instead of degrading to a blank surface.
 */

type FocusableUIManager = typeof UIManager & {
  hasViewManagerConfig?: (name: string) => boolean;
};

function resolveNative<T>(viewName: string, load: () => T): T | null {
  if (Platform.OS !== 'windows') {
    return null;
  }
  try {
    const manager = UIManager as FocusableUIManager;
    if (!manager.hasViewManagerConfig?.(viewName)) {
      return null;
    }
    return load();
  } catch {
    return null;
  }
}

let focusRing: React.ComponentType<any> | null | undefined;
let localImage: React.ComponentType<any> | null | undefined;

/** The native UI3 focus highlight, or null when unavailable. */
export function shellFocusRingComponent(): React.ComponentType<any> | null {
  if (focusRing === undefined) {
    focusRing = resolveNative(
      'ProsperismoFocusRing',
      () => require('./FocusRingNativeComponent').default,
    );
  }
  return focusRing ?? null;
}

/** The WIC-backed local image renderer, or null when unavailable. */
export function shellLocalImageComponent(): React.ComponentType<any> | null {
  if (localImage === undefined) {
    localImage = resolveNative(
      'ProsperismoLocalImage',
      () => require('./LocalImageNativeComponent').default,
    );
  }
  return localImage ?? null;
}

/** Test seam: forget cached resolutions. */
export function resetShellNativeComponents(): void {
  focusRing = undefined;
  localImage = undefined;
}
