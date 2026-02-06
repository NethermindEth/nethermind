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
public struct LogIndexAggregate(uint firstBlockNum, uint lastBlockNum)
{
    private Dictionary<Address, List<uint>>? _address;
    private Dictionary<Hash256, List<uint>>[]? _topic;

    public uint FirstBlockNum { get; } = firstBlockNum;
    public uint LastBlockNum { get; } = lastBlockNum;

    public Dictionary<Address, List<uint>> Address => _address ??= new();

    public Dictionary<Hash256, List<uint>>[] Topic => _topic ??= Enumerable.Range(0, LogIndexStorage.MaxTopics)
        .Select(static _ => new Dictionary<Hash256, List<uint>>())
        .ToArray();

    public bool IsEmpty => (_address is null || _address.Count == 0) && (_topic is null || _topic[0].Count == 0);
    public int TopicCount => _topic is { Length: > 0 } ? _topic.Sum(static t => t.Count) : 0;

    public LogIndexAggregate(IReadOnlyList<BlockReceipts> batch) : this(batch[0].BlockNumber, batch[^1].BlockNumber) { }
}
