import {
  gameStatusLabel,
  mergeCompatibilityEntries,
  parseCompatibilityDatabase,
  refreshCompatibilityDatabase,
  serializeCompatibilityDatabase,
} from '../src/core/compatibility';

test('parses Qt compatibility spellings and normalizes title IDs', () => {
  const entries = parseCompatibilityDatabase(JSON.stringify({
    ' ppsa00001 ': {status: 'In game', comment: 'renders'},
    PPSA00002: {status: "Doesn't boot", comment: 7},
  }));
  expect(entries).toEqual({
    PPSA00001: {status: 'InGame', comment: 'renders'},
    PPSA00002: {status: 'DoesntBoot', comment: ''},
  });
  expect(gameStatusLabel(entries.PPSA00002.status)).toBe("Doesn't boot");
  expect(JSON.parse(serializeCompatibilityDatabase(entries))).toEqual(entries);
});

test('schema-safe refresh keeps local edits and fills remote entries', () => {
  expect(mergeCompatibilityEntries(
    {PPSA1: {status: 'Logo', comment: 'remote'}, PPSA2: {status: 'InGame', comment: ''}},
    {PPSA1: {status: 'MainMenu', comment: 'my note'}},
  )).toEqual({
    PPSA1: {status: 'MainMenu', comment: 'my note'},
    PPSA2: {status: 'InGame', comment: ''},
  });
});

test('compatibility refresh matches Qt initial attempt plus three retries', async () => {
  const fetcher = jest.fn()
    .mockRejectedValueOnce(new Error('offline'))
    .mockResolvedValueOnce({ok: false, status: 503, text: async () => ''})
    .mockResolvedValueOnce({ok: true, status: 200, text: async () => JSON.stringify({PPSA1: {status: 'Logo'}})});
  const wait = jest.fn(async () => undefined);
  await expect(refreshCompatibilityDatabase(fetcher, wait)).resolves.toEqual({PPSA1: {status: 'Logo', comment: ''}});
  expect(fetcher).toHaveBeenCalledTimes(3);
  expect(wait.mock.calls).toEqual([[750], [1500]]);
});

test('rejects a non-object compatibility database', () => {
  expect(() => parseCompatibilityDatabase('[]')).toThrow('root must be an object');
});
