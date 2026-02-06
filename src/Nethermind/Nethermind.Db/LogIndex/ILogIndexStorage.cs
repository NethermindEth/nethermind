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
    /// Max block number added to the index.
    /// </summary>
    uint? MaxBlockNumber { get; }

    /// <summary>
    /// Min block number added to the index.
    /// </summary>
    uint? MinBlockNumber { get; }

    /// <summary>
    /// Gets enumerator of block numbers between <paramref name="from"/> and <paramref name="to"/>
    /// where given <paramref name="address"/> has occurred.
    /// </summary>
    IEnumerator<uint> GetEnumerator(Address address, uint from, uint to);

    /// <summary>
    /// Gets enumerator of block numbers between <paramref name="from"/> and <paramref name="to"/>
    /// where given <paramref name="topic"/> has occurred at the given <paramref name="topicIndex"/>.
    /// </summary>
    IEnumerator<uint> GetEnumerator(int topicIndex, Hash256 topic, uint from, uint to);

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
    Task RemoveReorgedAsync(BlockReceipts block);

    /// <summary>
    /// Forces compression of all the uncompressed values and subsequent DB compaction.
    /// </summary>
    Task CompactAsync(bool flush = false, int mergeIterations = 0, LogIndexUpdateStats? stats = null);

    string GetDbSize();
}
