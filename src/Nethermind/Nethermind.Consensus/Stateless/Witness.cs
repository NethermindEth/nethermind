// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

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
    // Resolved per use, not cached at static-init: a consensus plugin (e.g. AuRa) may register its
    // header decoder after this type is first touched, and caching would pin the base decoder.
    private static IRlpDecoder<BlockHeader> Decoder => Rlp.GetDecoderOrThrow<BlockHeader>();

    extension(Witness witness)
    {
        public INodeStorage CreateNodeStorage() => WitnessNodeStorage.Create(witness.State);

        /// <remarks>Codes that can also be read as memory are served as that memory, which code decoded into
        /// executable code memory then runs from without another copy.</remarks>
        public IKeyValueStoreWithBatching CreateCodeDb()
        {
            if (witness.Codes is IReadOnlyList<ReadOnlyMemory<byte>> codes) return new WitnessCodeDb(codes);

            IKeyValueStoreWithBatching db = MemDb.WithCapacity(witness.Codes.Count);
            foreach (byte[] code in witness.Codes)
            {
                ReadOnlySpan<byte> hash = ValueKeccak.Compute(code).Bytes;
                db.Set(hash, code);
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

                    decodedHeaders[i] = Decoder.Decode(ref reader)
                        ?? throw new InvalidOperationException($"No header decoded at index {i}");
                    reader.CheckEnd();

                    if (i > 0 && (decodedHeaders[i].ParentHash is null || decodedHeaders[i].ParentHash.ValueHash256 != previousHeaderHash))
                        throw new InvalidOperationException("Witness headers are not contiguous");

                    if (i + 1 < headersSpan.Length)
                    {
                        // The decoder hashes the header's own RLP, which is the whole of headers[i] once CheckEnd passed.
                        previousHeaderHash = decodedHeaders[i].Hash?.ValueHash256 ?? ValueKeccak.Compute(headers[i]);
                        Debug.Assert(previousHeaderHash == ValueKeccak.Compute(headers[i]), "Header decoder must set Hash to the keccak of the RLP it consumed");
                    }
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
