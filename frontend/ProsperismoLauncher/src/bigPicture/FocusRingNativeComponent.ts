/* eslint-disable @react-native/no-deep-imports -- codegen types are exposed through RN's internal utility paths. */
import type {ViewProps} from 'react-native';
import type {Double, Int32} from 'react-native/Libraries/Types/CodegenTypes';
import codegenNativeComponent from 'react-native/Libraries/Utilities/codegenNativeComponent';

interface FocusRingProps extends ViewProps {
  /** False runs the recovered focus-out (300ms out-motion, 200ms fade). */
  active: boolean;
  /** Target rect in the surface's design space; retargets start a warp. */
  targetX: Double;
  targetY: Double;
  targetWidth: Double;
  targetHeight: Double;
  /** Corner radius inherited from the focused widget. */
  radius: Double;
  /** Draw-time translation (entrance choreography), never a retarget. */
  offsetX: Double;
  offsetY: Double;
  /** Design-space size of the drawing surface stretched over this view. */
  surfaceWidth: Double;
  surfaceHeight: Double;
  /** Screen size for the 40% area gate and size falloff. */
  screenWidth: Double;
  screenHeight: Double;
  /** Increment to fire the recovered two-keyframe press pulse. */
  pressedToken: Int32;
  /** Doubles the fade-out rate while a d-pad direction repeats. */
  keyRepeating: boolean;
  /** Absolute path to the extracted image_focus_noise PNG. */
  noisePath: string;
}

/**
 * The recovered UI3 focus highlight, rendered natively: the wash and band are
 * the CPU distance-field rasters translated from SharpEmu's ShellFocusWash and
 * ShellFocusBand, driven by the full ShellFocusRingTimeline at 60 Hz.
 */
export default codegenNativeComponent<FocusRingProps>('ProsperismoFocusRing');
