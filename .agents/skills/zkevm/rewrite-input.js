// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Rewrites a ZisK-framed stateless input from one StatelessInput layout to the next.
// Template: this instance drops the trailing `public_keys` field removed in tests-zkevm@v21.0.1.
// Adapt OLD_FIXED/NEW_FIXED and the body rewrite to the release's schema change, and keep
// every check - the script must refuse any input that is not exactly the old layout.
// Usage: node rewrite-input.js <in> <out>
const fs = require('fs');
const [, , inPath, outPath] = process.argv;

// StatelessInput fixed part: offset(new_payload_request) | offset(witness) | chain_id u64 | offset(public_keys)
const OLD_FIXED = 4 + 4 + 8 + 4;
const NEW_FIXED = 4 + 4 + 8;
const PUBLIC_KEY_LENGTH = 65;

// ZisK frame: len u64le | data[len] | zero-padding to a multiple of 8
const frame = fs.readFileSync(inPath);
const dataLength = Number(frame.readBigUInt64LE(0));
if (8 + dataLength > frame.length) throw new Error(`framed length ${dataLength} exceeds file size ${frame.length}`);
for (let i = 8 + dataLength; i < frame.length; i++) if (frame[i] !== 0) throw new Error('non-zero frame padding');
const data = frame.subarray(8, 8 + dataLength);

// Data: schema u16be | SSZ StatelessInput
const schema = data.subarray(0, 2);
const body = data.subarray(2);
const requestOffset = body.readUInt32LE(0);
const witnessOffset = body.readUInt32LE(4);
const chainId = body.subarray(8, 16);
const keysOffset = body.readUInt32LE(16);

if (requestOffset !== OLD_FIXED) throw new Error(`not the old layout: first offset ${requestOffset}`);
if (!(requestOffset <= witnessOffset && witnessOffset <= keysOffset && keysOffset <= body.length)) throw new Error('offsets out of order');
const keysLength = body.length - keysOffset;
if (keysLength % PUBLIC_KEY_LENGTH !== 0) throw new Error(`public key tail ${keysLength} is not a multiple of ${PUBLIC_KEY_LENGTH}`);

// Offsets are relative to the container start, so each shifts by the fixed-part shrinkage.
const header = Buffer.alloc(NEW_FIXED);
header.writeUInt32LE(NEW_FIXED, 0);
header.writeUInt32LE(witnessOffset - (OLD_FIXED - NEW_FIXED), 4);
chainId.copy(header, 8);

const newData = Buffer.concat([schema, header, body.subarray(OLD_FIXED, keysOffset)]);
const lengthPrefix = Buffer.alloc(8);
lengthPrefix.writeBigUInt64LE(BigInt(newData.length));
const padding = Buffer.alloc((8 - (newData.length % 8)) % 8);
fs.writeFileSync(outPath, Buffer.concat([lengthPrefix, newData, padding]));

console.log(`${inPath}: schema 0x${schema.toString('hex')}, chain ${chainId.readBigUInt64LE()}, ` +
    `dropped ${keysLength / PUBLIC_KEY_LENGTH} keys, ${dataLength} -> ${newData.length} bytes`);
