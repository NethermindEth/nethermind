// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const fixtures = new URL('../../Nethermind.Evm.Test/', import.meta.url);
const address = number => `0x${BigInt(number).toString(16).padStart(40, '0')}`;
const maxModExpHexLength = (96 + 3 * 1024) * 2;

async function readFixture(name) {
    return JSON.parse(await fs.readFile(new URL(`PrecompileVectors/${name}.json`, fixtures), 'utf8'));
}

/** Run shared execution-client vectors through an initialized browser provider. */
export async function runPrecompileVectors(request, report = console.log) {
    let passed = 0;
    const results = {};
    const call = (number, input) => request({
        method: 'eth_call',
        params: [{ to: address(number), data: `0x${input}`, gas: '0x989680' }, 'finalized']
    });

    async function check(number, input, expected, name) {
        const start = performance.now();
        assert.equal(await call(number, input), `0x${expected}`, name);
        passed++;
        results[number] = (results[number] ?? 0) + 1;
        report(`${name}: ${(performance.now() - start).toFixed(1)} ms`);
    }

    async function reject(number, input, name) {
        await assert.rejects(call(number, input), name);
        passed++;
        results[number] = (results[number] ?? 0) + 1;
    }

    for (const name of ['modexp', 'modexp_eip2565', 'modexp_eip7883']) {
        let selected = 0;
        for (const vector of await readFixture(name)) {
            if (vector.Input.length > maxModExpHexLength || vector.ExpectedError || vector.Expected == null) continue;
            // Current mainnet applies EIP-7823's 1024-byte operand bound.
            if (vector.Input.length >= 192 && [0, 64, 128].some(offset =>
                BigInt(`0x${vector.Input.slice(offset, offset + 64)}`) > 1024n)) continue;
            await check(5, vector.Input, vector.Expected, vector.Name);
            selected++;
        }
        assert.ok(selected > 0, `No ModExp vectors discovered in ${name}.`);
    }

    for (const [number, name] of [[6, 'BN254AddPrecompileTests'], [7, 'BN254MulPrecompileTests'], [8, 'BN254PairingCheckPrecompileTests']]) {
        const source = await fs.readFile(new URL(`${name}.cs`, fixtures), 'utf8');
        const cases = [...source.matchAll(/\[TestCase\("([0-9a-f]*)", "([0-9a-f]*)", (true|false)\)\]/g)];
        assert.ok(cases.length > 0, `No BN254 vectors discovered in ${name}.`);
        for (const match of cases) {
            if (match[3] === 'true') await check(number, match[1], match[2], name);
            else await reject(number, match[1], `${name}: invalid point`);
        }
    }

    const p256 = (await readFixture('p256Verify')).slice(0, 32);
    assert.equal(p256.length, 32, 'Wycheproof P256 fixtures are missing.');
    for (const vector of p256) await check(256, vector.Input, vector.Expected, vector.Name);

    const kzgSource = await fs.readFile(new URL('KzgPointEvaluationPrecompileTests.cs', fixtures), 'utf8');
    const start = kzgSource.indexOf('private static IEnumerable<TestCaseData> ValidTestCases');
    assert.ok(start >= 0, 'KZG valid fixture section is missing.');
    const kzg = [...kzgSource.slice(start).matchAll(/CreateKzgTestInput\(\s*"([0-9a-f]+)",\s*"([0-9a-f]+)",\s*"([0-9a-f]+)",\s*"([0-9a-f]+)",\s*"([0-9a-f]+)"/g)];
    assert.ok(kzg.length >= 6, 'KZG proof fixtures are missing.');
    const kzgResult = '0'.repeat(60) + '1000' + '73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001';
    for (const match of kzg) await check(10, match.slice(1).join(''), kzgResult, 'KZG valid proof');
    const validProof = kzg[5].slice(1).join('');
    const changedByte = validProof.endsWith('00') ? '01' : '00';
    await reject(10, validProof.slice(0, -2) + changedByte, 'KZG corrupted proof');

    for (const [number, name] of [[11, 'add_G1_bls'], [12, 'multiexp_G1_bls'], [13, 'add_G2_bls'], [14, 'multiexp_G2_bls'], [15, 'pairing_check_bls'], [16, 'map_fp_to_G1_bls'], [17, 'map_fp2_to_G2_bls']]) {
        const vectors = await readFixture(`Bls/${name}`);
        assert.ok(vectors.length >= 4, `BLS vectors are missing in ${name}.`);
        for (const vector of [...vectors.slice(0, 2), ...vectors.slice(-2)]) {
            assert.ok(vector.Input.length < 10000, `BLS smoke vector exceeds its size bound: ${vector.Name}.`);
            await check(number, vector.Input, vector.Expected, vector.Name);
        }
        const invalid = await readFixture(`Bls/fail-${name}`);
        assert.ok(invalid.length >= 2, `BLS invalid vectors are missing in ${name}.`);
        for (const vector of invalid.slice(0, 2)) await reject(number, vector.Input, vector.Name);
    }

    report(`${passed} browser precompile vectors passed.`);
    return { passed, results };
}
