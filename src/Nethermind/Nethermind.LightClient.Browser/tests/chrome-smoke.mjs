// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { chromium } from 'playwright';
import { secp256k1 } from '@noble/curves/secp256k1.js';
import { keccak_256 } from '@noble/hashes/sha3.js';
import { runPrecompileVectors } from './precompiles.browser.js';

const { values } = parseArgs({ options: {
    bundle: { type: 'string', default: fileURLToPath(new URL('../../artifacts/publish/Nethermind.LightClient.Browser/release/wwwroot', import.meta.url)) },
    chrome: { type: 'string' },
    fixtures: { type: 'string' }
} });
assert.ok(values.fixtures, 'Pass --fixtures <directory> containing the generated signed consensus and MPT fixtures.');
const bundle = path.resolve(values.bundle);
const fixtures = path.resolve(values.fixtures);
const proof = JSON.parse(await fs.readFile(path.join(fixtures, 'synthetic-proof.json'), 'utf8'));
const payloads = Object.fromEntries(await Promise.all(['bootstrap', 'update', 'finality', 'optimistic'].map(async kind =>
    [kind, await fs.readFile(path.join(fixtures, `synthetic-${kind}.ssz`))])));
const header = await fs.readFile(path.join(fixtures, 'synthetic-header.rlp'));
let bootstrapRequests = 0;
let forgedProof = false;
let forgedCode = false;
let forgedSignature = false;
const unexpectedMethods = [];
const pageErrors = [];

const server = http.createServer(async (request, response) => {
    const url = new URL(request.url, 'http://localhost');
    try {
        if (url.pathname === '/favicon.ico') { response.writeHead(204).end(); return; }
        if (url.pathname.startsWith('/browser/')) {
            const file = path.resolve(bundle, url.pathname.slice('/browser/'.length) || 'index.html');
            assert.ok(file.startsWith(bundle + path.sep), 'Static file escaped the published bundle.');
            response.setHeader('content-type', file.endsWith('.wasm') ? 'application/wasm' :
                file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' :
                    file.endsWith('.html') ? 'text/html' : 'application/octet-stream');
            response.setHeader('Content-Security-Policy', "default-src 'self';script-src 'self' 'wasm-unsafe-eval';style-src 'self';worker-src 'self';connect-src 'self' https:");
            response.end(await fs.readFile(file));
            return;
        }
        if (url.pathname.startsWith('/lightclient/bootstrap/')) {
            bootstrapRequests++;
            response.end(payloads.bootstrap);
            return;
        }
        if (url.pathname === '/lightclient/updates') {
            const update = Buffer.from(payloads.update);
            if (forgedSignature) update[update.length - 100] ^= 1;
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify([update.toString('base64')]));
            return;
        }
        if (url.pathname === '/lightclient/finality') { response.end(payloads.finality); return; }
        if (url.pathname === '/lightclient/optimistic') { response.end(payloads.optimistic); return; }
        if (url.pathname.startsWith('/lightclient/header/')) { response.end(header); return; }
        if (url.pathname.startsWith('/lightclient/code/')) {
            response.end(Buffer.from(forgedCode ? '00' : proof.code.slice(2), 'hex'));
            return;
        }
        if (request.method === 'POST') {
            let body = '';
            for await (const chunk of request) {
                body += chunk;
                assert.ok(body.length <= 1024 * 1024, 'Unexpected oversized proof-relay request.');
            }
            const rpc = JSON.parse(body);
            let result;
            if (rpc.method === 'eth_getProof') {
                // Contradictory metadata proves that accepted values come from the local trie verifier.
                result = { address: rpc.params[0], balance: '0xffff', nonce: '0xff', codeHash: '0x' + 'ff'.repeat(32),
                    storageHash: '0x' + 'ff'.repeat(32),
                    accountProof: forgedProof ? [proof.accountProof[0].slice(0, -2) + '01'] : proof.accountProof,
                    storageProof: rpc.params[1].map(key => ({ key, value: '0xffff', proof: [] })) };
            } else if (rpc.method === 'eth_getCode') {
                result = forgedCode ? '0x00' : proof.code;
            } else {
                unexpectedMethods.push(rpc.method);
                throw new Error(`Unexpected upstream RPC ${rpc.method}`);
            }
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ jsonrpc: '2.0', id: rpc.id, result }));
            return;
        }
        response.writeHead(404).end();
    } catch (error) {
        response.writeHead(500).end(error.message);
    }
});

let browser;
try {
    await new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', resolve);
    });
    const origin = `http://127.0.0.1:${server.address().port}`;
    browser = await chromium.launch({ executablePath: values.chrome, headless: true });
    const page = await browser.newPage();
    page.on('pageerror', error => pageErrors.push(error.message));
    await page.goto(`${origin}/browser/`);
    const connect = () => page.evaluate(async checkpoint => {
        const { createProvider } = await import('./provider.js');
        globalThis.provider = await createProvider({ network: 'mainnet', checkpoint, relays: [location.origin] });
        return provider.request({ method: 'eth_chainId' });
    }, proof.checkpoint);
    const request = rpc => page.evaluate(async rpc => provider.request(rpc), rpc);
    const get = (method, params) => request({ method, params });
    assert.equal(await connect(), '0x1');
    assert.equal(await get('eth_getBalance', [proof.address, 'finalized']), '0x7b');
    assert.equal(await get('eth_getTransactionCount', [proof.address, 'finalized']), '0x7');
    assert.equal(await get('eth_getBalance', ['0x' + '00'.repeat(20), 'finalized']), '0x0');
    assert.equal(await get('eth_getStorageAt', [proof.address, '0x1', 'finalized']), '0x' + '00'.repeat(32));
    assert.equal(await get('eth_getCode', [proof.address, 'finalized']), proof.code);
    assert.equal(await get('eth_call', [{ to: proof.address, data: '0x' }, 'finalized']), '0x' + '00'.repeat(31) + '2a');
    const gas = await get('eth_estimateGas', [{ to: proof.address, data: '0x' }, 'finalized']);
    assert.ok(BigInt(gas) >= 21000n && BigInt(gas) < 100000n);

    const secret = new Uint8Array(32);
    secret[31] = 1;
    const digest = new Uint8Array(32);
    const recovered = Buffer.from(secp256k1.sign(digest, secret, { prehash: false, format: 'recovered' }));
    const recoveryData = '0x' + Buffer.from(digest).toString('hex') + (27 + recovered[0]).toString(16).padStart(64, '0') + recovered.subarray(1).toString('hex');
    const expected = '0x' + '00'.repeat(12) + Buffer.from(keccak_256(secp256k1.getPublicKey(secret, false).subarray(1))).subarray(12).toString('hex');
    const recoverAddress = '0x' + '00'.repeat(19) + '01';
    assert.equal(await get('eth_call', [{ to: recoverAddress, data: recoveryData }, 'finalized']), expected);
    assert.equal(await get('eth_call', [{ to: recoverAddress, data: '0x' + '00'.repeat(128) }, 'finalized']), '0x');

    forgedProof = true;
    await assert.rejects(get('eth_getBalance', [proof.address, 'finalized']));
    forgedProof = false;
    forgedCode = true;
    await assert.rejects(get('eth_getCode', [proof.address, 'finalized']));
    forgedCode = false;
    await page.evaluate(() => provider.disconnect());
    assert.equal(await connect(), '0x1');
    assert.equal(bootstrapRequests, 1);
    assert.equal(await get('eth_getBalance', [proof.address, 'finalized']), '0x7b');

    await page.evaluate(async () => {
        provider.disconnect();
        const database = await new Promise((resolve, reject) => {
            const open = indexedDB.open('nethermind-lightclient', 1);
            open.onsuccess = () => resolve(open.result);
            open.onerror = () => reject(open.error);
        });
        await new Promise((resolve, reject) => {
            const transaction = database.transaction('journals', 'readwrite');
            const cursorRequest = transaction.objectStore('journals').openCursor();
            cursorRequest.onsuccess = () => {
                const cursor = cursorRequest.result;
                if (cursor) {
                    const record = cursor.value;
                    record.chain = record.chain.slice(0, -8) + 'AAAAAAAA';
                    cursor.update(record);
                }
            };
            transaction.oncomplete = resolve;
            transaction.onerror = () => reject(transaction.error);
        });
        database.close();
    });
    assert.equal(await connect(), '0x1');
    assert.equal(bootstrapRequests, 2);
    await page.evaluate(async () => {
        provider.disconnect();
        await new Promise((resolve, reject) => {
            const deletion = indexedDB.deleteDatabase('nethermind-lightclient');
            deletion.onsuccess = resolve;
            deletion.onerror = () => reject(deletion.error);
        });
    });
    forgedSignature = true;
    await assert.rejects(connect());
    forgedSignature = false;
    assert.equal(await connect(), '0x1');
    const vectors = await runPrecompileVectors(request);
    assert.deepEqual(unexpectedMethods, [], 'Browser execution used an upstream RPC result.');
    assert.deepEqual(pageErrors, [], 'The browser raised an uncaught error.');
    console.log(JSON.stringify({ result: 'PASS', vectors }));
} finally {
    try {
        if (browser) await browser.close();
    } finally {
        if (server.listening) {
            server.closeAllConnections();
            await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
        }
    }
}
