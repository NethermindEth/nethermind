// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { verifyEngineResponse } from './engine_response.mjs';

const hash = '0x' + 'a'.repeat(64);
const other = '0x' + 'b'.repeat(64);
for (const kind of ['newPayloadV4', 'forkchoiceUpdatedV3']) {
    const payload = kind.startsWith('newPayload');
    const request = { id: 17, method: 'engine_' + kind,
        params: [payload ? { blockHash: hash } : { headBlockHash: hash }] };
    const status = { status: 'VALID', latestValidHash: hash, validationError: null };
    const reply = { jsonrpc: '2.0', id: 17,
        result: payload ? status : { payloadStatus: status, payloadId: null } };
    const response = body => ({ status: 200, body: JSON.stringify(body) });
    test(kind + ': accepts exact VALID/hash response', () => {
        assert.equal(verifyEngineResponse(response(reply), request), hash);
    });
    for (const bad of ['INVALID', 'SYNCING', 'ACCEPTED', 'INVALID_BLOCK_HASH']) {
        test(kind + ': rejects ' + bad + ' over HTTP 200', () => {
            const body = structuredClone(reply);
            (payload ? body.result : body.result.payloadStatus).status = bad;
            assert.throws(() => verifyEngineResponse(response(body), request));
        });
    }
    for (const [name, value] of [['wrong hash', other], ['null hash', null]]) {
        test(kind + ': rejects ' + name, () => {
            const body = structuredClone(reply);
            (payload ? body.result : body.result.payloadStatus).latestValidHash = value;
            assert.throws(() => verifyEngineResponse(response(body), request));
        });
    }
    for (const body of [{ jsonrpc: '2.0', id: 17, error: { code: -32603 } },
                        { ...reply, id: 18 }, { ...reply, id: undefined },
                        { ...reply, jsonrpc: '1.0' }, { ...reply, result: null }]) {
        test(kind + ': rejects malformed or unsuccessful JSON-RPC ' + JSON.stringify(body), () => {
            assert.throws(() => verifyEngineResponse(response(body), request));
        });
    }
    test(kind + ': rejects malformed JSON and HTTP failure', () => {
        assert.throws(() => verifyEngineResponse({ status: 200, body: '{' }, request));
        assert.throws(() => verifyEngineResponse({ status: 500, body: JSON.stringify(reply) }, request));
        const body = structuredClone(reply);
        (payload ? body.result : body.result.payloadStatus).validationError = 'invalid state';
        assert.throws(() => verifyEngineResponse(response(body), request));
    });
}
