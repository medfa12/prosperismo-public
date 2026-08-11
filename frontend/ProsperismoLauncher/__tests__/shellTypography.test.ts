import {
  SHELL_CLOCK_TEXT_STYLE,
  SHELL_FONT_CANDIDATES,
  SHELL_FONT_FAMILY,
  SHELL_FONT_SIZE_PS,
  resolveShellFontFamily,
  shellTextStyle,
} from '../src/bigPicture/shellTypography';

describe('shell typography', () => {
  test('uses the UI3 font-size token values recovered from firmware metadata', () => {
    expect(SHELL_FONT_SIZE_PS).toEqual({
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
    });
  });

  test('accepts only an audited family reported by the native resolver', () => {
    for (const family of SHELL_FONT_CANDIDATES) {
      expect(resolveShellFontFamily(family)).toBe(family);
    }
    expect(resolveShellFontFamily('SST')).toBe('Segoe UI');
    expect(resolveShellFontFamily('')).toBe('Segoe UI');
    expect(resolveShellFontFamily(undefined)).toBe('Segoe UI');
  });

  test('builds consistent shell styles from symbolic tokens', () => {
    expect(shellTextStyle('SizeXSmall', '600')).toEqual({
      fontFamily: SHELL_FONT_FAMILY,
      fontSize: 24,
      fontWeight: '600',
    });
  });

  test('keeps the recovered tabular clock contract', () => {
    expect(SHELL_CLOCK_TEXT_STYLE).toMatchObject({
      fontSize: 36,
      fontVariant: ['tabular-nums'],
      textAlign: 'right',
    });
  });
});
