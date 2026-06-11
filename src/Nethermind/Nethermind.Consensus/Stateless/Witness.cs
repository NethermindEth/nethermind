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
    private static readonly IRlpDecoder<BlockHeader> _decoder =
        Rlp.GetDecoder<BlockHeader>() ?? new HeaderDecoder();

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
            ReadOnlySpan<byte[]> headersSpan = headers.AsSpan();
            ArrayPoolList<BlockHeader> decodedHeaders = new(headersSpan.Length, headersSpan.Length);

            // Witness headers must form a contiguous chain: each header's parent hash must equal the
            // hash (keccak of the RLP) of the preceding header. Linkage is by parent hash, not a
            // block-number comparison (that check lives in the header validator), though a well-formed
            // chain is thereby ordered by ascending block number. This mirrors the stateless verifier's
            // rule in EELS (validate_headers) and rejects witnesses whose headers were reordered or are
            // otherwise non-contiguous. The previous header's hash is carried across iterations so each
            // keccak is computed once.
            try
            {
                ValueHash256 previousHeaderHash = default;

                for (int i = 0; i < headersSpan.Length; i++)
                {
                    RlpReader reader = new(headersSpan[i]);

                    BlockHeader decodedHeader = _decoder.Decode(ref reader)
                        ?? throw new InvalidOperationException($"No header decoded at index {i}");
                    decodedHeaders[i] = decodedHeader;

                    if (i > 0 && (decodedHeader.ParentHash is null || decodedHeader.ParentHash.ValueHash256 != previousHeaderHash))
                        throw new InvalidOperationException("Witness headers are not contiguous");

                    previousHeaderHash = ValueKeccak.Compute(headers[i]);
                }

                return decodedHeaders;
            }
            catch
            {
                decodedHeaders.Dispose();
                throw;
            }
        }
    }
}
