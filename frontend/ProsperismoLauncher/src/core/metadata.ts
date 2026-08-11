import type {GameMetadata} from './models';

type JsonObject = Record<string, unknown>;

function objectValue(value: unknown): JsonObject | undefined {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? (value as JsonObject)
    : undefined;
}

function stringValue(root: JsonObject | undefined, key: string): string {
  const value = root?.[key];
  return typeof value === 'string' ? value.trim() : '';
}

export function decodeRequiredFirmware(encoded: unknown): string {
  if (typeof encoded !== 'string') {
    return '';
  }
  const match = /^0[xX]([0-9]{6})[0-9A-Fa-f]{10}$/.exec(encoded.trim());
  if (!match) {
    return '';
  }
  const digits = match[1];
  const major = Number.parseInt(digits.slice(0, 2), 10);
  if (!Number.isFinite(major)) {
    return '';
  }
  const base = `${major}.${digits.slice(2, 4)}`;
  return digits.slice(4, 6) === '00' ? base : `${base}.${digits.slice(4, 6)}`;
}

function localizedTitle(root: JsonObject): string {
  const localized = objectValue(root.localizedParameters);
  if (!localized) {
    return '';
  }
  const defaultLanguage = stringValue(localized, 'defaultLanguage');
  if (defaultLanguage) {
    const title = stringValue(objectValue(localized[defaultLanguage]), 'titleName');
    if (title) {
      return title;
    }
  }
  const englishTitle = stringValue(objectValue(localized['en-US']), 'titleName');
  if (englishTitle) {
    return englishTitle;
  }
  for (const value of Object.values(localized)) {
    const title = stringValue(objectValue(value), 'titleName');
    if (title) {
      return title;
    }
  }
  return '';
}

export function parseParamJson(text: string | undefined, fallback: string): GameMetadata {
  let root: JsonObject | undefined;
  try {
    root = objectValue(text ? JSON.parse(text) : undefined);
  } catch {
    root = undefined;
  }
  if (!root) {
    return {titleName: fallback, titleId: '', gameVersion: '', firmwareVersion: ''};
  }
  return {
    titleName: localizedTitle(root) || fallback,
    titleId: stringValue(root, 'titleId'),
    gameVersion: stringValue(root, 'appVersion') || stringValue(root, 'contentVersion'),
    firmwareVersion: decodeRequiredFirmware(root.requiredSystemSoftwareVersion),
  };
}
