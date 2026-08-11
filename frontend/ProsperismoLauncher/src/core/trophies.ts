/* eslint-disable no-bitwise -- UCP fields and UTF-8 code units are binary formats. */
export type TrophyGrade = 'Platinum' | 'Gold' | 'Silver' | 'Bronze' | string;

export interface TrophyRow {
  id: string;
  name: string;
  detail: string;
  grade: TrophyGrade;
  reward: string;
  hidden: boolean;
  hasReward: boolean;
  icon?: Uint8Array;
}

export interface TrophySet {
  title: string;
  trophies: TrophyRow[];
}

interface UcpEntry {
  name: string;
  contents: Uint8Array;
}

const UCP_MAGIC = 0xb228c60a;
const UCP_VERSION = 1;
const UCP_HEADER_LENGTH = 0x40;
const UCP_TOC_SKIP = 0x20;
const UCP_ENTRY_LENGTH = 0x40;
const UCP_NAME_LENGTH = 0x20;

function readBe32(data: Uint8Array, offset: number): number {
  return ((data[offset] << 24) | (data[offset + 1] << 16) |
    (data[offset + 2] << 8) | data[offset + 3]) >>> 0;
}

function readBe64Safe(data: Uint8Array, offset: number): number {
  const high = readBe32(data, offset);
  const low = readBe32(data, offset + 4);
  const value = high * 0x100000000 + low;
  if (!Number.isSafeInteger(value)) {
    throw new Error('Trophy package contains an address larger than JavaScript can represent safely.');
  }
  return value;
}

function fixedLatin1(data: Uint8Array, offset: number, length: number): string {
  let end = offset;
  while (end < offset + length && data[end] !== 0) {
    end += 1;
  }
  return Array.from(data.slice(offset, end), byte => String.fromCharCode(byte)).join('').trim();
}

export function readUcpEntries(data: Uint8Array): Map<string, Uint8Array> {
  if (data.length < UCP_HEADER_LENGTH) {
    throw new Error('Trophy package is too small.');
  }
  if (readBe32(data, 0) !== UCP_MAGIC) {
    throw new Error('Invalid trophy package magic.');
  }
  const version = readBe32(data, 4);
  if (version !== UCP_VERSION) {
    throw new Error(`Unsupported trophy package version ${version}.`);
  }
  const declaredSize = readBe64Safe(data, 8);
  if (declaredSize > data.length) {
    throw new Error('Trophy package is truncated.');
  }
  const fileCount = readBe32(data, 0x10);
  const tocOffset = readBe32(data, 0x14);
  const tableEnd = tocOffset + UCP_TOC_SKIP + fileCount * UCP_ENTRY_LENGTH;
  if (!Number.isSafeInteger(tableEnd) || tocOffset > data.length || tableEnd > data.length) {
    throw new Error('Trophy package has an invalid table of contents.');
  }
  const entries: UcpEntry[] = [];
  for (let index = 0; index < fileCount; index += 1) {
    const entryOffset = tocOffset + UCP_TOC_SKIP + index * UCP_ENTRY_LENGTH;
    const name = fixedLatin1(data, entryOffset, UCP_NAME_LENGTH);
    const offset = readBe64Safe(data, entryOffset + 0x20);
    const size = readBe64Safe(data, entryOffset + 0x28);
    if (!name) {
      continue;
    }
    if (offset > data.length || size > data.length - offset) {
      throw new Error(`Trophy package has an invalid entry for ${name}.`);
    }
    entries.push({name: name.toLocaleLowerCase('en-US'), contents: data.slice(offset, offset + size)});
  }
  return new Map(entries.map(entry => [entry.name, entry.contents]));
}

function jsonObject(bytes: Uint8Array, name: string): Record<string, unknown> {
  let value: unknown;
  try {
    value = JSON.parse(decodeUtf8(bytes));
  } catch (reason) {
    throw new Error(`Could not read ${name}: ${reason instanceof Error ? reason.message : String(reason)}`);
  }
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${name} must contain a JSON object.`);
  }
  return value as Record<string, unknown>;
}

function decodeUtf8(bytes: Uint8Array): string {
  let result = '';
  for (let index = 0; index < bytes.length;) {
    const first = bytes[index++];
    if (first < 0x80) {
      result += String.fromCharCode(first);
      continue;
    }
    const continuationCount = first < 0xe0 ? 1 : first < 0xf0 ? 2 : first < 0xf8 ? 3 : -1;
    if (continuationCount < 0 || index + continuationCount > bytes.length) {
      result += '\ufffd';
      continue;
    }
    let codePoint = first & (0x7f >> continuationCount);
    let valid = true;
    for (let part = 0; part < continuationCount; part += 1) {
      const next = bytes[index++];
      if ((next & 0xc0) !== 0x80) {
        valid = false;
        break;
      }
      codePoint = (codePoint << 6) | (next & 0x3f);
    }
    if (!valid || codePoint > 0x10ffff) {
      result += '\ufffd';
    } else if (codePoint <= 0xffff) {
      result += String.fromCharCode(codePoint);
    } else {
      const adjusted = codePoint - 0x10000;
      result += String.fromCharCode(0xd800 + (adjusted >> 10), 0xdc00 + (adjusted & 0x3ff));
    }
  }
  return result;
}

function scalarText(value: unknown): string {
  return typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean'
    ? String(value).trim()
    : '';
}

function gradeText(value: string): TrophyGrade {
  return ({P: 'Platinum', G: 'Gold', S: 'Silver', B: 'Bronze'} as Record<string, TrophyGrade>)[value] ?? value;
}

function findIcon(entries: Map<string, Uint8Array>, id: string): Uint8Array | undefined {
  const names = [`trop${id}.png`];
  const numeric = Number.parseInt(id, 10);
  if (Number.isFinite(numeric)) {
    names.push(`trop${String(numeric).padStart(4, '0')}.png`);
  }
  return names.map(name => entries.get(name.toLocaleLowerCase('en-US'))).find(Boolean);
}

export function parseTrophyPackage(data: Uint8Array, fileName = 'Trophy.ucp'): TrophySet {
  const entries = readUcpEntries(data);
  const confBytes = entries.get('tropconf.json');
  if (!confBytes) {
    throw new Error(`${fileName} does not contain tropconf.json.`);
  }
  const conf = jsonObject(confBytes, 'tropconf.json');
  const language = scalarText(conf.defaultLanguage);
  const metaBytes = (language ? entries.get(`tropmeta_${language}`.toLocaleLowerCase('en-US') + '.json') : undefined) ??
    [...entries].find(([name]) => name.startsWith('tropmeta_') && name.endsWith('.json'))?.[1] ??
    entries.get('tropmeta.json');
  if (!metaBytes) {
    throw new Error(`${fileName} does not contain readable trophy metadata.`);
  }
  const meta = jsonObject(metaBytes, 'tropmeta.json');
  const metadataObject = meta.metadata && typeof meta.metadata === 'object' && !Array.isArray(meta.metadata)
    ? meta.metadata as Record<string, unknown>
    : {};
  const texts = new Map<string, {name: string; detail: string; reward: string}>();
  const metadataRows = Array.isArray(metadataObject.trophyMetadata) ? metadataObject.trophyMetadata : [];
  metadataRows.forEach(raw => {
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
      return;
    }
    const row = raw as Record<string, unknown>;
    const id = scalarText(row.id);
    if (id) {
      texts.set(id, {name: scalarText(row.name), detail: scalarText(row.detail), reward: scalarText(row.reward)});
    }
  });
  const definitions = Array.isArray(conf.trophies) ? conf.trophies : [];
  const trophies = definitions.flatMap(raw => {
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
      return [];
    }
    const definition = raw as Record<string, unknown>;
    const id = scalarText(definition.id);
    if (!id) {
      return [];
    }
    const hidden = definition.hidden === true;
    const text = texts.get(id) ?? {name: '', detail: '', reward: ''};
    return [{
      id,
      name: text.name || (hidden ? 'Hidden Trophy' : `Trophy ${id}`),
      detail: text.detail || (hidden ? 'This trophy is hidden.' : ''),
      grade: gradeText(scalarText(definition.grade)),
      reward: text.reward,
      hidden,
      hasReward: definition.hasReward === true,
      icon: findIcon(entries, id),
    }];
  });
  if (trophies.length === 0) {
    throw new Error(`${fileName} does not define any trophies.`);
  }
  const match = /^trophy(\d+)\.ucp$/i.exec(fileName.replace(/^.*[\\/]/, ''));
  return {title: match ? `Trophy ${match[1]}` : fileName.replace(/^.*[\\/]|\.ucp$/gi, ''), trophies};
}
