// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createProvider } from '../wwwroot/provider.js';

class FakeWorker {
    static latest;
    constructor() { FakeWorker.latest = this; }
    postMessage(message) {
        if (message.action === 'initialize') {
            queueMicrotask(() => this.onmessage({ data: { id: message.id, result: { chainId: '0x1' } } }));
        } else this.last = message;
    }
    terminate() { this.terminated = true; }
    respond(data) { this.onmessage({ data: { id: this.last.id, ...data } }); }
}
globalThis.Worker = FakeWorker;

test('provider returns results and preserves provider errors', async () => {
    const provider = await createProvider({});
    try {
        let request = provider.request({ method: 'eth_getBalance', params: ['0x00', 'finalized'] });
        FakeWorker.latest.respond({ result: '0x123' });
        assert.equal(await request, '0x123');
        request = provider.request({ method: 'eth_call' });
        FakeWorker.latest.respond({ error: { code: 3, message: 'execution reverted', data: '0x1234' } });
        await assert.rejects(request, { code: 3, message: 'execution reverted', data: '0x1234' });
    } finally { provider.disconnect(); }
});

test('provider bounds admission and rejects pending work on disconnect', async () => {
    const provider = await createProvider({});
    const pending = Array.from({ length: 16 }, () => provider.request({ method: 'eth_chainId' }));
    await assert.rejects(provider.request({ method: 'eth_chainId' }), { code: -32005 });
    provider.disconnect();
    const results = await Promise.allSettled(pending);
    assert.ok(results.every(result => result.status === 'rejected' && result.reason.code === 4900));
    assert.equal(FakeWorker.latest.terminated, true);
    await assert.rejects(provider.request({ method: 'eth_chainId' }), { code: 4900 });
});

test('provider validates parameters and message size before posting to the worker', async () => {
    const provider = await createProvider({});
    try {
        for (const request of [{}, { method: 1 }, { method: 'eth_chainId', params: {} }]) {
            await assert.rejects(provider.request(request), { code: -32602 });
        }
        await assert.rejects(provider.request({ method: 'eth_getCode', params: ['x'.repeat(1024 * 1024)] }), { code: -32602 });
        const cyclic = [];
        cyclic.push(cyclic);
        for (const params of [cyclic, [1n]]) {
            await assert.rejects(provider.request({ method: 'eth_call', params }), { code: -32602 });
        }
        assert.equal(FakeWorker.latest.last, undefined);
    } finally { provider.disconnect(); }
});

test('a failed worker post releases its request slot', async () => {
    const provider = await createProvider({});
    const worker = FakeWorker.latest;
    const post = worker.postMessage;
    worker.postMessage = () => { throw new Error('Worker stopped.'); };
    try {
        for (let attempt = 0; attempt < 20; attempt++) {
            await assert.rejects(provider.request({ method: 'eth_chainId' }), { message: 'Worker stopped.' });
        }
        worker.postMessage = post;
        const request = provider.request({ method: 'eth_chainId' });
        worker.respond({ result: '0x1' });
        assert.equal(await request, '0x1');
    } finally { provider.disconnect(); }
});

test('provider delivers diagnostic messages and removes listeners', async () => {
    const provider = await createProvider({});
    try {
        const received = [];
        const listener = value => received.push(value);
        provider.on('message', listener);
        FakeWorker.latest.onmessage({ data: { event: 'message', data: { type: 'nethermind_sync_error', data: 'Peer unavailable.' } } });
        assert.equal(received.length, 1);
        provider.removeListener('message', listener);
        FakeWorker.latest.onmessage({ data: { event: 'message', data: {} } });
        assert.equal(received.length, 1);
    } finally { provider.disconnect(); }
});
