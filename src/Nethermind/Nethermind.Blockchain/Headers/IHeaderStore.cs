// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Headers;

public interface IHeaderStore : IHeaderFinder
{
    void Insert(BlockHeader header);

    /// <summary>
    /// Stores <paramref name="header"/>, visible to readers at once, with its database write queued off the caller's
    /// path where deferred block-data persistence is on; otherwise the same as <see cref="Insert"/>.
    /// </summary>
    void InsertDeferred(BlockHeader header) => Insert(header);
    void BulkInsert(IReadOnlyList<BlockHeader> headers);
    BlockHeader? Get(Hash256 blockHash, bool shouldCache, ulong? blockNumber = null);

    /// <summary>
    /// Gets the header of <paramref name="blockHash"/> if <paramref name="bloomMatches"/> accepts its logs bloom, which
    /// is empty for a zero-length (EIP-7668) bloom.
    /// </summary>
    /// <param name="blockHash">The hash of the block.</param>
    /// <param name="blockNumber">The number of the block.</param>
    /// <param name="bloomMatches">Tells whether the bloom is a match.</param>
    /// <param name="found">Whether the header is stored, whether or not its bloom is a match.</param>
    /// <remarks>The bloom may be tested on the stored encoding, so a header it rejects need not be decoded.</remarks>
    BlockHeader? GetIfBloomMatches(Hash256 blockHash, ulong blockNumber, Func<ReadOnlySpan<byte>, bool> bloomMatches, out bool found)
    {
        BlockHeader? header = Get(blockHash, shouldCache: false, blockNumber);
        found = header is not null;
        return found && bloomMatches(header!.Bloom!.Bytes) ? header : null;
    }
    void Cache(BlockHeader header);
    void Delete(Hash256 blockHash);
    void InsertBlockNumber(Hash256 blockHash, ulong blockNumber);
    ulong? GetBlockNumber(Hash256 blockHash);

    /// <summary>
    /// Returns up to <paramref name="count"/> consecutive headers ending at <paramref name="endBlockHash"/>,
    /// walking backward through parent hashes. Uses a DB iterator for bulk read when the underlying
    /// store supports <see cref="ISortedKeyValueStore"/>; otherwise falls back to per-hash lookups.
    /// The returned list is ordered oldest-first. If the chain breaks, the returned list is shorter.
    /// Returns an empty list when <paramref name="endBlockHash"/> is not found.
    /// </summary>
    IOwnedReadOnlyList<BlockHeader> FindReversedHeaders(ulong endBlockNumber, Hash256 endBlockHash, int count);

    Dictionary<ValueHash256, BlockHeader> PrefetchByNumberRange(ulong fromInclusive, ulong toExclusive) => [];
}
