// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Db.LogIndex;

/// <summary>
/// Set of in-memory dictionaries mapping each address/topic to a sequence of block numbers
/// from the <paramref name="firstBlockNum"/> - <paramref name="lastBlockNum"/> range.
/// </summary>
public struct LogIndexAggregate(int firstBlockNum, int lastBlockNum, bool isBackwardSync)
{
    private Dictionary<Address, List<int>>? _address;
    private Dictionary<Hash256, List<int>>[]? _topic;

    public int FirstBlockNum { get; } = firstBlockNum;
    public int LastBlockNum { get; } = lastBlockNum;

    /// <summary>The direction the batch was collected in, carried rather than re-derived: a batch of one block has
    /// the same first and last number, so comparing them reads a backward batch as a forward one and sends it to the
    /// wrong writer.</summary>
    public bool IsBackwardSync { get; } = isBackwardSync;

    public Dictionary<Address, List<int>> Address => _address ??= [];

    public Dictionary<Hash256, List<int>>[] Topic => _topic ??= Enumerable.Range(0, LogIndexStorage.MaxTopics)
        .Select(static _ => new Dictionary<Hash256, List<int>>())
        .ToArray();

    public bool IsEmpty => (_address is null || _address.Count == 0) && (_topic is null || _topic[0].Count == 0);
    public int TopicCount => _topic is { Length: > 0 } ? _topic.Sum(static t => t.Count) : 0;

    public LogIndexAggregate(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync)
        : this(batch[0].BlockNumber, batch[^1].BlockNumber, isBackwardSync) { }
}
