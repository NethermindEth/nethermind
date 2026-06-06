// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Collections;

namespace Nethermind.Consensus.Stateless;

public class WitnessGeneratingHeaderFinder(IHeaderFinder inner) : IHeaderFinder
{
    private static readonly HeaderDecoder _decoder = new();
    // Not thread-safe: a pooled rent is reset then used on a single thread, so no synchronization is
    // needed. Concurrent use of one instance would require it.
    private long _lowestRequestedHeader = long.MaxValue;

    /// <summary>Resets BLOCKHASH bookkeeping so this instance can be reused across pooled rents.</summary>
    public void Reset() => _lowestRequestedHeader = long.MaxValue;

    public BlockHeader? Get(Hash256 blockHash, long? blockNumber = null)
    {
        BlockHeader? header = inner.Get(blockHash, blockNumber);
        if (header is not null && header.Number < _lowestRequestedHeader)
        {
            _lowestRequestedHeader = header.Number;
        }
        return header;
    }

    public IOwnedReadOnlyList<byte[]> GetWitnessHeaders(Hash256 parentHash)
    {
        Hash256 currentHash = parentHash;
        BlockHeader parentHeader = inner.Get(currentHash) ?? throw new ArgumentException($"Parent {currentHash} is not found");

        // Headers are emitted in ascending block-number order (oldest first and the recorded block's
        // parent last) so they form a contiguous chain, matching the stateless verifier's expectation.
        //
        // Only the parent is captured unless ancestor headers were requested during processing
        // (e.g. BLOCKHASH reaching further back), tracked by _lowestRequestedHeader.
        int count = _lowestRequestedHeader < long.MaxValue
            ? (int)(parentHeader.Number - _lowestRequestedHeader + 1)
            : 1;
        int index = count - 1;
        ArrayPoolList<byte[]> headers = new(count, count);
        try
        {
            headers[index--] = _decoder.Encode(parentHeader).Bytes;

            if (index >= 0)
            {
                for (long i = parentHeader.Number - 1; i >= _lowestRequestedHeader; i--)
                {
                    currentHash = parentHeader.ParentHash!;
                    parentHeader = inner.Get(currentHash, i)
                        ?? throw new ArgumentException($"Unable to get requested header at hash {currentHash} and number {i} during witness generation");
                    headers[index--] = _decoder.Encode(parentHeader).Bytes;
                }
            }

            return headers;
        }
        catch
        {
            // A missing ancestor (reorg/prune mid-walk) leaves the partially-filled list holding pooled
            // buffers — return them before propagating.
            headers.Dispose();
            throw;
        }
    }
}
