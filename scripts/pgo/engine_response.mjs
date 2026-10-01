// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

export function verifyEngineResponse(response, request) {
    if (response.status !== 200) throw new Error('Engine API HTTP failure');
    const reply = JSON.parse(response.body);
    if (!reply || reply.jsonrpc !== '2.0' || reply.id === undefined || request.id === undefined ||
        reply.id !== request.id || reply.error != null)
        throw new Error('Engine API JSON-RPC failure');
    const payload = request.method.startsWith('engine_newPayload');
    if (!payload && !request.method.startsWith('engine_forkchoiceUpdated'))
        throw new Error('Unexpected Engine API method');
    const expected = payload ? request.params[0].blockHash : request.params[0].headBlockHash;
    if (typeof expected !== 'string' || !/^0x[0-9a-fA-F]{64}$/.test(expected))
        throw new Error('Invalid expected block hash');
    const status = payload ? reply.result : reply.result?.payloadStatus;
    if (!status || status.status !== 'VALID' || status.validationError !== null ||
        typeof status.latestValidHash !== 'string' || status.latestValidHash.toLowerCase() !== expected.toLowerCase())
        throw new Error('Engine API payload status or hash failure');
    return expected.toLowerCase();
}
