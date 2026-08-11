/* eslint-disable no-bitwise -- the fixture writes big-endian UCP fields. */
import {parseTrophyPackage, readUcpEntries} from '../src/core/trophies';

const write32 = (data: Uint8Array, offset: number, value: number) => {
  data[offset] = value >>> 24; data[offset + 1] = value >>> 16; data[offset + 2] = value >>> 8; data[offset + 3] = value;
};
const write64 = (data: Uint8Array, offset: number, value: number) => { write32(data, offset, 0); write32(data, offset + 4, value); };
const utf8 = (text: string) => Uint8Array.from(unescape(encodeURIComponent(text)), value => value.charCodeAt(0));

function packageWith(files: Record<string, string>): Uint8Array {
  const names = Object.keys(files);
  const tocOffset = 0x40;
  const dataOffset = tocOffset + 0x20 + names.length * 0x40;
  const encoded = names.map(name => utf8(files[name]));
  const result = new Uint8Array(dataOffset + encoded.reduce((sum, item) => sum + item.length, 0));
  write32(result, 0, 0xb228c60a); write32(result, 4, 1); write64(result, 8, result.length); write32(result, 0x10, names.length); write32(result, 0x14, tocOffset);
  let cursor = dataOffset;
  names.forEach((name, index) => {
    const entry = tocOffset + 0x20 + index * 0x40;
    [...name].forEach((character, characterIndex) => { result[entry + characterIndex] = character.charCodeAt(0); });
    write64(result, entry + 0x20, cursor); write64(result, entry + 0x28, encoded[index].length);
    result.set(encoded[index], cursor); cursor += encoded[index].length;
  });
  return result;
}

test('parses bounded UCP trophy definitions and localized UTF-8 metadata', () => {
  const data = packageWith({
    'tropconf.json': JSON.stringify({defaultLanguage: 'en-US', trophies: [{id: 1, grade: 'G', hidden: false, hasReward: true}]}),
    'tropmeta_en-US.json': JSON.stringify({metadata: {trophyMetadata: [{id: 1, name: 'Héro', detail: 'Done', reward: 'Hat'}]}}),
  });
  expect(parseTrophyPackage(data, 'Trophy02.ucp')).toMatchObject({title: 'Trophy 02', trophies: [{id: '1', name: 'Héro', grade: 'Gold', reward: 'Hat'}]});
});

test('rejects invalid UCP magic rather than reading guessed offsets', () => {
  expect(() => readUcpEntries(new Uint8Array(0x40))).toThrow('magic');
});
