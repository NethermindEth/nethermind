// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

const maxBytes = 7 * 1024 * 1024;

/** Stores journal envelopes in one IndexedDB transaction, keyed by network and trusted checkpoint. */
export class BrowserJournal {
    constructor(network, checkpoint) {
        this.key = `${network}:${checkpoint.toLowerCase()}`;
    }

    async open() {
        this.database = await new Promise((resolve, reject) => {
            const request = indexedDB.open('nethermind-lightclient', 1);
            request.onupgradeneeded = () => request.result.createObjectStore('journals');
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
            request.onblocked = () => reject(new Error('Browser journal database upgrade is blocked.'));
        });
        this.database.onversionchange = () => this.database.close();
        return this;
    }

    async read() {
        const value = await this.transaction('readonly', store => store.get(this.key));
        if (value && (typeof value.chain !== 'string' || typeof value.head !== 'string' || typeof value.optimistic !== 'string' || typeof value.best !== 'string'
            || value.chain.length + value.head.length + value.optimistic.length + value.best.length > maxBytes)) {
            await this.clear();
            return null;
        }
        return value;
    }

    async write(value) {
        if (value.chain.length + value.head.length + value.optimistic.length + value.best.length > maxBytes) throw new Error('Browser journal reached its size limit.');
        await this.transaction('readwrite', store => store.put(value, this.key));
    }

    clear() {
        return this.transaction('readwrite', store => store.delete(this.key));
    }

    transaction(mode, action) {
        return new Promise((resolve, reject) => {
            const transaction = this.database.transaction('journals', mode);
            const request = action(transaction.objectStore('journals'));
            transaction.oncomplete = () => resolve(request.result);
            transaction.onerror = () => reject(transaction.error ?? request.error);
            transaction.onabort = () => reject(transaction.error ?? new Error('Browser journal write was aborted.'));
        });
    }
}
