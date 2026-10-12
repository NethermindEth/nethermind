// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { IDBFactory } from 'fake-indexeddb';
import { BrowserJournal } from '../wwwroot/journal.js';

test('journal writes every envelope together and isolates checkpoint/network keys', async () => {
    globalThis.indexedDB = new IDBFactory();
    const first = await new BrowserJournal('mainnet', '0xAa').open();
    const same = await new BrowserJournal('mainnet', '0xaa').open();
    const other = await new BrowserJournal('hoodi', '0xaa').open();
    try {
        const record = { chain: 'abc', head: 'def', optimistic: 'ghi', best: 'jkl' };
        await first.write(record);
        assert.deepEqual(await same.read(), record);
        assert.equal(await other.read(), undefined);
        await first.clear();
        assert.equal(await same.read(), undefined);
    } finally { for (const journal of [first, same, other]) journal.database.close(); }
});

test('journal discards malformed or oversized saved envelopes', async () => {
    globalThis.indexedDB = new IDBFactory();
    const journal = await new BrowserJournal('mainnet', '0xaa').open();
    try {
        for (const record of [{ chain: {}, head: '', optimistic: '', best: '' }, { chain: '', head: null, optimistic: '', best: '' },
            { chain: '', head: '', optimistic: null, best: '' }, { chain: '', head: '', optimistic: '', best: null },
            { chain: 'x'.repeat(7 * 1024 * 1024 + 1), head: '', optimistic: '', best: '' }]) {
            await journal.transaction('readwrite', store => store.put(record, journal.key));
            assert.equal(await journal.read(), null);
            assert.equal(await journal.read(), undefined);
        }
        await assert.rejects(journal.write({ chain: 'x'.repeat(7 * 1024 * 1024 + 1), head: '', optimistic: '', best: '' }), /size limit/);
    } finally { journal.database.close(); }
});
