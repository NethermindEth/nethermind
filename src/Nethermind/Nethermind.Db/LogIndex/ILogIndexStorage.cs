// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Db.LogIndex;

public interface ILogIndexStorage : IAsyncDisposable, IStoppableService
{
    bool Enabled { get; }

    /// <summary>
    /// Max block number added to the index, or null if none was added or the index stopped at a reorg it could not revert.
    /// </summary>
    int? MaxBlockNumber { get; }

    /// <summary>
    /// Min block number added to the index.
    /// </summary>
    int? MinBlockNumber { get; }

    /// <summary>
    /// Gets enumerator of block numbers between <paramref name="from"/> and <paramref name="to"/>
    /// where given <paramref name="address"/> has occurred.
    /// </summary>
    IEnumerator<int> GetEnumerator(Address address, int from, int to);

    /// <summary>
    /// Gets enumerator of block numbers between <paramref name="from"/> and <paramref name="to"/>
    /// where given <paramref name="topic"/> has occurred at the given <paramref name="topicIndex"/>.
    /// </summary>
    IEnumerator<int> GetEnumerator(int topicIndex, Hash256 topic, int from, int to);

    /// <summary>
    /// Aggregates receipts from the <paramref name="batch"/> into in-memory <see cref="LogIndexAggregate"/>.
    /// </summary>
    LogIndexAggregate Aggregate(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null);

    /// <summary>
    /// Adds receipts from the <paramref name="aggregate"/> to the index.
    /// </summary>
    Task AddReceiptsAsync(LogIndexAggregate aggregate, LogIndexUpdateStats? stats = null);

    /// <summary>
    /// Removes reorged <paramref name="block"/> from the index.
    /// This must be called for each reorged block in a sequential ascending order.
    /// </summary>
    /// <remarks>
    /// A block deeper than the max reorg depth below the highest max block reached may already be compressed and cannot be reverted:
    /// the index is then capped below it, <see cref="MaxBlockNumber"/> becomes null so queries stop using the index,
    /// further forward writes throw until the index is reset, and this call throws.
    /// The depth is the smallest max reorg depth the index has run with.
    /// Only blocks added in forward sync are removed: blocks added in backward sync are kept.
    /// </remarks>
    Task RemoveReorgedAsync(BlockReceipts block);

    string GetDbSize();
}
