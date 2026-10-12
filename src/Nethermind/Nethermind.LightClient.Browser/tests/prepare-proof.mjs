// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import fs from 'node:fs/promises';
import path from 'node:path';
import { parseArgs } from 'node:util';
import { keccak_256 } from '@noble/hashes/sha3.js';

const { values } = parseArgs({ options: { fixtures: { type: 'string' } } });
const directory = values.fixtures ?? (process.platform === 'win32'
    ? 'C:/Temp/nethermind-lightclient-browser-smoke' : '/tmp/nethermind-lightclient-browser-smoke');
if (!path.isAbsolute(directory)) throw new Error('The fixture directory must be an absolute temporary path.');
const bytes = value => Uint8Array.from(Buffer.from(value.replace(/^0x/, ''), 'hex'));
const concat = (...arrays) => Uint8Array.from(arrays.flatMap(array => [...array]));
const prefix = (length, base) => {
    if (length < 56) return Uint8Array.of(base + length);
    const size = Math.ceil(length.toString(16).length / 2);
    return concat(Uint8Array.of(base + 55 + size), bytes(length.toString(16).padStart(size * 2, '0')));
};
const rlp = value => {
    if (Array.isArray(value)) {
        const body = concat(...value.map(rlp));
        return concat(prefix(body.length, 0xc0), body);
    }
    return value.length === 1 && value[0] < 128 ? value : concat(prefix(value.length, 0x80), value);
};
const hex = value => `0x${Buffer.from(value).toString('hex')}`;
const address = '0x1234567890123456789012345678901234567890';
const code = '0x602a60005260206000f3';
const storageHash = hex(keccak_256(rlp(new Uint8Array())));
const account = rlp([bytes('07'), bytes('7b'), bytes(storageHash), keccak_256(bytes(code))]);
const leaf = rlp([concat(bytes('20'), keccak_256(bytes(address))), account]);
await fs.mkdir(directory, { recursive: true });
await fs.writeFile(path.join(directory, 'synthetic-proof.json'), JSON.stringify({
    address, code, storageHash, codeHash: hex(keccak_256(bytes(code))),
    stateRoot: hex(keccak_256(leaf)), accountProof: [hex(leaf)], balance: '0x7b', nonce: '0x7',
}));
console.log(`Independent RLP/MPT fixture written to ${directory}`);
