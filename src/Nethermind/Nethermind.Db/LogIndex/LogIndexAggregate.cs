// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Db.LogIndex;

public struct LogIndexAggregate<TPos>(int firstBlockNum, int lastBlockNum)
    where TPos: struct, ILogPosition<TPos>
{
    private Dictionary<Address, IList<TPos>>? _address;
    private Dictionary<Hash256, IList<TPos>>[]? _topic;

    public int FirstBlockNum { get; } = firstBlockNum;
    public int LastBlockNum { get; } = lastBlockNum;

    public Dictionary<Address, IList<TPos>> Address => _address ??= new();

    public Dictionary<Hash256, IList<TPos>>[] Topic => _topic ??= Enumerable.Range(0, LogIndexStorage<TPos>.MaxTopics)
        .Select(static _ => new Dictionary<Hash256, IList<TPos>>())
        .ToArray();

    public bool IsEmpty => (_address is null || _address.Count == 0) && (_topic is null || _topic[0].Count == 0);
    public int TopicCount => _topic is { Length: > 0 } ? _topic.Sum(static t => t.Count) : 0;

    public LogIndexAggregate(IReadOnlyList<BlockReceipts> batch) : this(batch[0].BlockNumber, batch[^1].BlockNumber) { }
}
