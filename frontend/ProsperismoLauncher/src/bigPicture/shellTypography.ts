import {NativeModules, type TextStyle} from 'react-native';

/**
 * FontSizePS values recovered from the UI3 managed metadata.
 *
 * Keep these symbolic names at call sites. Sony's RN bundles use the tokens,
 * not ad-hoc pixel values, and the token relationships are part of the shell's
 * visual hierarchy.
 */
export const SHELL_FONT_SIZE_PS = {
  Size3XLarge: 72,
  Size2XLarge: 54,
  SizeXLarge: 45,
  SizeLarge: 36,
  SizeNormal: 30,
  SizeSmall: 27,
  SizeXSmall: 24,
  Size2XSmall: 21,
  Size3XSmall: 18,
  Size4XSmall: 15,
} as const;

export type ShellFontSizeToken = keyof typeof SHELL_FONT_SIZE_PS;
export type ShellFontWeight = '400' | '500' | '600' | '700';

/**
 * Fira Sans is the audited OFL substitute for Sony SST. React Native Windows
 * 0.83's Fabric text path only sees DirectWrite's system collection, so a
 * native resolver reports Fira when the user has it installed. On stock
 * Windows 11 it selects the closer variable text cut, then the guaranteed
 * Segoe UI fallback. No Sony font is read, copied, installed, or packaged.
 */
export const SHELL_FONT_CANDIDATES = [
  'Fira Sans',
  'Segoe UI Variable Text',
  'Segoe UI',
] as const;

export type ShellFontFamily = (typeof SHELL_FONT_CANDIDATES)[number];

type ShellTypographyNativeConstants = {
  fontFamily?: unknown;
  source?: unknown;
  firaSansAvailable?: unknown;
};

type ShellTypographyNativeModule = ShellTypographyNativeConstants & {
  getConstants?: () => ShellTypographyNativeConstants;
};

function isShellFontFamily(value: unknown): value is ShellFontFamily {
  return typeof value === 'string'
    && (SHELL_FONT_CANDIDATES as readonly string[]).includes(value);
}

/** Pure resolver kept exported so the native/JS boundary is regression tested. */
export function resolveShellFontFamily(nativeFamily?: unknown): ShellFontFamily {
  return isShellFontFamily(nativeFamily) ? nativeFamily : 'Segoe UI';
}

function readNativeConstants(): ShellTypographyNativeConstants {
  const module = NativeModules.ShellTypography as ShellTypographyNativeModule | undefined;
  if (!module) {
    return {};
  }

  // Attributed C++ modules surface constant-provider values directly on most
  // RNW builds. Keep getConstants support for bridgeless/Turbo interop.
  try {
    const constants = typeof module.getConstants === 'function'
      ? module.getConstants()
      : module;
    return constants && typeof constants === 'object' ? constants : {};
  } catch {
    return {};
  }
}

const nativeConstants = readNativeConstants();

export const SHELL_FONT_FAMILY = resolveShellFontFamily(nativeConstants.fontFamily);

export const SHELL_FONT_DIAGNOSTICS = Object.freeze({
  family: SHELL_FONT_FAMILY,
  source: typeof nativeConstants.source === 'string'
    ? nativeConstants.source
    : 'system-fallback',
  firaSansAvailable: nativeConstants.firaSansAvailable === true,
});

/**
 * Produces the invariant part of a shell Text style. Keeping the family and
 * Sony token in one helper prevents utility/settings surfaces drifting back
 * to arbitrary desktop typography.
 */
export function shellTextStyle(
  size: ShellFontSizeToken,
  weight: ShellFontWeight = '400',
): TextStyle {
  return {
    fontFamily: SHELL_FONT_FAMILY,
    fontSize: SHELL_FONT_SIZE_PS[size],
    fontWeight: weight,
  };
}

/** Exact HOME clock numeric feature. */
export const SHELL_CLOCK_TEXT_STYLE: TextStyle = {
  ...shellTextStyle('SizeLarge'),
  fontVariant: ['tabular-nums'],
  textAlign: 'right',
};
