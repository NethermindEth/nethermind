// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import { dotnet } from './_framework/dotnet.js';
import { BrowserJournal } from './journal.js';

let verifier;
let options;
let journal;
let status;
let queue = Promise.resolve();
let requestId = 0;
let queuedRequests = 0;
let syncPending = false;

self.onmessage = ({ data }) => {
    if (++queuedRequests > 16) {
        queuedRequests--;
        self.postMessage({ id: data.id, error: { code: -32005, message: 'Too many browser requests.' } });
        return;
    }
    queue = queue.then(async () => {
        try {
            const result = data.action === 'initialize' ? await initialize(data.parameters)
                : await request(data.parameters);
            self.postMessage({ id: data.id, result });
        } catch (error) {
            self.postMessage({ id: data.id, error: {
                code: Number.isInteger(error.code) ? error.code : -32000, message: error.message,
                ...(error.data === undefined ? {} : { data: error.data }),
            } });
        } finally { queuedRequests--; }
    });
};

async function initialize(configuration) {
    if (verifier) throw new Error('The browser light client is already initialized.');
    if (!['mainnet', 'hoodi', 'sepolia'].includes(configuration.network ?? 'mainnet')
        || !/^0x[0-9a-f]{64}$/i.test(configuration.checkpoint)
        || !Array.isArray(configuration.relays) || configuration.relays.length < 1 || configuration.relays.length > 4) {
        throw Object.assign(new Error('Supply a network, a trusted finalized checkpoint and one to four proof relay URLs.'), { code: -32602 });
    }
    options = { ...configuration, network: configuration.network ?? 'mainnet' };
    options.relays = configuration.relays.map(value => {
        const url = new URL(value, self.location.href);
        if (url.protocol !== 'https:' && !(url.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname))) {
            throw new Error('Proof relays must use HTTPS, except on loopback.');
        }
        if (url.username || url.password || url.search || url.hash) throw new Error('Proof relay URLs must not contain credentials, a query or a fragment.');
        return url.href.replace(/\/$/, '');
    });
    const runtime = await dotnet.create();
    await runtime.runMain();
    verifier = (await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName)).BrowserExports;
    const setup = await fetchBytes(new URL('./kzg_trusted_setup.txt', import.meta.url), 1024 * 1024);
    await verifier.InitializeKzg(base64(setup));
    let saved;
    try {
        journal = await new BrowserJournal(options.network, options.checkpoint).open();
        saved = await journal.read();
    } catch {
        journal = null;
        self.postMessage({ event: 'message', data: { type: 'nethermind_storage_unavailable', data: 'Browser storage is unavailable; consensus will be verified again after reload.' } });
    }
    if (saved) {
        try {
            status = JSON.parse(await verifier.Initialize(options.network, options.checkpoint, '', saved.chain, saved.head, saved.optimistic, saved.best));
        } catch {
            try { await journal.clear(); } catch { journal = null; }
            self.postMessage({ event: 'message', data: { type: 'nethermind_journal_recovery', data: 'Saved records failed verification; requesting the original trusted checkpoint again.' } });
        }
    }
    if (!status) {
        await tryRelays(async relay => {
            const bootstrap = await fetchBytes(`${relay}/lightclient/bootstrap/${options.checkpoint}`, 256 * 1024);
            status = JSON.parse(await verifier.Initialize(options.network, options.checkpoint, base64(bootstrap), '', '', '', ''));
        });
    }
    verifier.ConfigureExecution(JSON.stringify(options.relays));
    await sync();
    await persist();
    setInterval(() => {
        if (syncPending) return;
        syncPending = true;
        queue = queue.then(sync)
            .catch(error => self.postMessage({ event: 'message', data: { type: 'nethermind_sync_error', data: error.message } }))
            .finally(() => { syncPending = false; });
    }, 12_000);
    return status;
}

async function sync() {
    await tryRelays(async relay => {
        for (let attempt = 0; attempt < 8 && (status.period < status.currentPeriod || !status.nextCommitteeKnown); attempt++) {
            const period = status.period;
            const nextKnown = status.nextCommitteeKnown;
            const start = period + (nextKnown ? 1 : 0);
            const data = await fetchJson(`${relay}/lightclient/updates?start_period=${start}&count=8`, 3 * 1024 * 1024);
            if (!Array.isArray(data) || data.length > 8) throw new Error('Proof relay returned an invalid update range.');
            for (const record of data) {
                if (typeof record !== 'string') throw new Error('Proof relay returned an invalid SSZ update.');
                status = JSON.parse(await verifier.Apply('update', record));
            }
            status = JSON.parse(await verifier.RecoverAfterTimeout());
            if (data.length === 0 || status.period === period && status.nextCommitteeKnown === nextKnown) break;
        }
        for (const kind of ['finality', 'optimistic']) {
            const record = await fetchBytes(`${relay}/lightclient/${kind}`, 256 * 1024);
            status = JSON.parse(await verifier.Apply(kind, base64(record)));
        }
    });
    await persist();
}

async function persist() {
    if (!journal) return;
    const exported = JSON.parse(await verifier.ExportJournal());
    try { await journal.write(exported); }
    catch {
        journal.database.close();
        journal = null;
        self.postMessage({ event: 'message', data: { type: 'nethermind_storage_unavailable', data: 'Browser storage could not retain the verified journal; this session continues in memory.' } });
    }
}

async function request({ method, params }) {
    if (!verifier || !status) throw Object.assign(new Error('Browser verifier is not connected.'), { code: 4900 });
    if (method === 'eth_chainId') {
        requireCount(params, 0);
        return status.chainId;
    }
    if (method === 'eth_accounts') {
        requireCount(params, 0);
        return [];
    }
    if (method === 'eth_blockNumber') {
        requireCount(params, 0);
        return (await head('latest')).blockNumber;
    }
    if (method === 'eth_call' || method === 'eth_estimateGas') {
        if (params.length < 1 || params.length > 2 || !params[0] || typeof params[0] !== 'object' || Array.isArray(params[0])) {
            throw Object.assign(new Error('Invalid transaction object.'), { code: -32602 });
        }
        const selector = params[1] ?? 'latest';
        if (!['latest', 'finalized'].includes(selector)) {
            throw Object.assign(new Error('Browser state selectors are finalized or latest.'), { code: -32602 });
        }
        const reply = JSON.parse(await verifier.Execute(selector, JSON.stringify(params[0]), method === 'eth_estimateGas'));
        if (reply.error) throw Object.assign(new Error(reply.error.message), reply.error);
        return reply.result;
    }
    if (!['eth_getBalance', 'eth_getTransactionCount', 'eth_getStorageAt', 'eth_getCode', 'eth_getProof'].includes(method)) {
        throw Object.assign(new Error(`Unsupported browser provider method: ${method}`), { code: 4200 });
    }
    const storage = method === 'eth_getStorageAt';
    const proofMethod = method === 'eth_getProof';
    requireCount(params, storage || proofMethod ? 3 : 2);
    const address = params[0];
    if (!/^0x[0-9a-f]{40}$/i.test(address)) throw Object.assign(new Error('Invalid account address.'), { code: -32602 });
    const selected = params[storage || proofMethod ? 2 : 1];
    if (!['latest', 'finalized'].includes(selected)) {
        throw Object.assign(new Error('Browser state selectors are finalized or latest.'), { code: -32602 });
    }
    const keys = storage ? [params[1]] : proofMethod ? params[1] : [];
    if (!Array.isArray(keys) || keys.length > 64 || keys.some(key => !/^0x[0-9a-f]{1,64}$/i.test(key))) {
        throw Object.assign(new Error('Invalid storage key list.'), { code: -32602 });
    }
    const authenticated = await head(selected);
    return await tryRelays(async relay => {
        const supplied = await rpc(relay, 'eth_getProof', [address, keys, { blockHash: authenticated.blockHash, requireCanonical: true }]);
        const accountProof = JSON.stringify(supplied.accountProof);
        const account = JSON.parse(verifier.VerifyAccount(selected, address, accountProof));
        if (method === 'eth_getBalance') return account.balance;
        if (method === 'eth_getTransactionCount') return account.nonce;
        if (method === 'eth_getCode') {
            const code = await rpc(relay, 'eth_getCode', [address, { blockHash: authenticated.blockHash, requireCanonical: true }]);
            return verifier.VerifyCode(selected, address, accountProof, code);
        }
        const storageProof = keys.map(key => {
            const entry = supplied.storageProof?.find(item => BigInt(item.key) === BigInt(key));
            if (!entry) throw new Error('Proof relay omitted a requested storage proof.');
            const value = verifier.VerifyStorage(selected, address, accountProof,
                `0x${BigInt(key).toString(16).padStart(64, '0')}`, JSON.stringify(entry.proof));
            return { key, value: `0x${BigInt(value).toString(16)}`, proof: entry.proof };
        });
        if (storage) return `0x${BigInt(storageProof[0].value).toString(16).padStart(64, '0')}`;
        return { address, ...account, accountProof: supplied.accountProof, storageProof };
    });
}

async function head(selector) {
    let result = JSON.parse(verifier.GetHead(selector));
    if (!result.stateRoot) {
        await tryRelays(async relay => {
            const data = await fetchBytes(`${relay}/lightclient/header/${result.blockHash}`, 2048);
            verifier.AuthenticateExecutionHeader(selector, base64(data));
        });
        result = JSON.parse(verifier.GetHead(selector));
    }
    return result;
}

async function rpc(relay, method, params) {
    const id = ++requestId;
    const reply = await fetchJson(relay, 4 * 1024 * 1024, {
        method: 'POST', headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ jsonrpc: '2.0', id, method, params }),
    });
    if (reply.jsonrpc !== '2.0' || reply.id !== id) throw new Error('Proof relay returned an unmatched JSON-RPC response.');
    if (reply.error) throw Object.assign(new Error(reply.error.message ?? 'Proof relay RPC failed.'), { code: reply.error.code });
    if (!('result' in reply)) throw new Error('Proof relay returned an invalid JSON-RPC response.');
    return reply.result;
}

async function tryRelays(operation) {
    let error;
    for (const relay of options.relays) {
        try { return await operation(relay); }
        catch (failure) { error = failure; }
    }
    throw error;
}

async function fetchJson(url, maximum, init) {
    const bytes = await fetchBytes(url, maximum, init);
    return JSON.parse(new TextDecoder().decode(bytes));
}

async function fetchBytes(url, maximum, init = {}) {
    const response = await fetch(url, { ...init, credentials: 'omit', redirect: 'error', signal: AbortSignal.timeout(30_000) });
    if (!response.ok || Number(response.headers.get('content-length')) > maximum) {
        await response.body?.cancel();
        throw new Error(response.ok ? 'Proof relay response exceeds its size limit.' : `Proof relay returned HTTP ${response.status}.`);
    }
    const reader = response.body.getReader();
    let result = new Uint8Array(Math.min(maximum, 8192));
    let length = 0;
    try {
        while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            length += value.length;
            if (length > maximum) throw new Error('Proof relay response exceeds its size limit.');
            if (length > result.length) {
                const grown = new Uint8Array(Math.min(maximum, Math.max(length, result.length * 2)));
                grown.set(result);
                result = grown;
            }
            result.set(value, length - value.length);
        }
    } catch (error) {
        await reader.cancel();
        throw error;
    } finally { reader.releaseLock(); }
    return result.subarray(0, length);
}

function base64(bytes) {
    let binary = '';
    for (let index = 0; index < bytes.length; index += 8192) {
        binary += String.fromCharCode(...bytes.subarray(index, index + 8192));
    }
    return btoa(binary);
}

function requireCount(params, count) {
    if (params.length !== count) throw Object.assign(new Error('Invalid provider parameters.'), { code: -32602 });
}
