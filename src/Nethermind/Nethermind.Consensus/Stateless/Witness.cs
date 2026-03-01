// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

public class Witness : IDisposable
{
    public required IOwnedReadOnlyList<byte[]> Codes { get; init; }
    public required IOwnedReadOnlyList<byte[]> State { get; init; }
    public required IOwnedReadOnlyList<byte[]> Keys { get; init; }
    public required IOwnedReadOnlyList<byte[]> Headers { get; init; }

    public void Dispose()
    {
        Codes.Dispose();
        State.Dispose();
        Keys.Dispose();
        Headers.Dispose();
    }
}

public static class WitnessExtensions
{
    private static readonly HeaderDecoder _decoder = new();

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
            IOwnedReadOnlyList<byte[]> headers = witness.Headers;
            ArrayPoolList<BlockHeader> decodedHeaders = new(headers.Count, headers.Count);

            for (int i = 0; i < headers.Count; i++)
            {
                Rlp.ValueDecoderContext stream = new(headers[i]);

                decodedHeaders[i] = _decoder.Decode(ref stream)
                    ?? throw new InvalidOperationException($"No header decoded at index {i}");
            }

            return decodedHeaders;
        }
    }
}
