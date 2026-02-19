// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

public class Witness
{
    public byte[][] Codes;
    public byte[][] State;
    public byte[][] Keys;
    public byte[][] Headers;
}

public static class WitnessExtensions
{
    extension(Witness witness)
    {
        public INodeStorage CreateNodeStorage()
        {
            IKeyValueStore db = new MemDb();
            foreach (byte[] stateElement in witness.State)
            {
                ReadOnlySpan<byte> hash = ValueKeccak.Compute(stateElement).Bytes;
                db.PutSpan(hash, stateElement);
            }

            return new NodeStorage(db, INodeStorage.KeyScheme.Hash);
        }

        public IKeyValueStoreWithBatching CreateCodeDb()
        {
            IKeyValueStoreWithBatching db = new MemDb();
            foreach (byte[] code in witness.Codes)
            {
                ReadOnlySpan<byte> hash = ValueKeccak.Compute(code).Bytes;
                db.PutSpan(hash, code);
            }

            return db;
        }

        public ArrayPoolList<BlockHeader> DecodeHeaders()
        {
            byte[][] witnessHeaders = witness.Headers;
            ArrayPoolList<BlockHeader> headers = new(witnessHeaders.Length);
            HeaderDecoder decoder = new();
            foreach (byte[] encodedHeader in witnessHeaders)
            {
                Rlp.ValueDecoderContext stream = new(encodedHeader);
                headers.Add(decoder.Decode(ref stream) ?? throw new ArgumentException());
            }

            return headers;
        }
    }
}
