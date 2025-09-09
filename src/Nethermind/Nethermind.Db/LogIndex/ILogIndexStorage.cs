// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Db;

public readonly record struct BlockReceipts(int BlockNumber, TxReceipt[] Receipts);

public struct LogIndexAggregate(int firstBlockNum, int lastBlockNum)
{
    private Dictionary<Address, List<int>>? _address;
    private Dictionary<Hash256, List<int>>[]? _topic;

    public int FirstBlockNum { get; } = firstBlockNum;
    public int LastBlockNum { get; } = lastBlockNum;

    public Dictionary<Address, List<int>> Address => _address ??= new();

    public Dictionary<Hash256, List<int>>[] Topic => _topic ??= Enumerable.Range(0, LogIndexStorage.MaxTopics)
        .Select(static _ => new Dictionary<Hash256, List<int>>())
        .ToArray();

    public bool IsEmpty => (_address is null || _address.Count == 0) && (_topic is null || _topic[0].Count == 0);
    public int TopicCount => _topic is { Length: > 0 } ? _topic.Sum(static t => t.Count) : 0;

    public LogIndexAggregate(IReadOnlyList<BlockReceipts> batch) : this(batch[0].BlockNumber, batch[^1].BlockNumber) { }
}

// TODO: remove testing methods
public interface ILogIndexStorage : IAsyncDisposable, IStoppableService
{
    int? GetMaxBlockNumber();
    int? GetMinBlockNumber();

    List<int> GetBlockNumbersFor(Address address, int from, int to);
    List<int> GetBlockNumbersFor(int index, Hash256 topic, int from, int to);

    Dictionary<byte[], int[]> GetKeysFor(Address address, int from, int to, bool includeValues = false);
    Dictionary<byte[], int[]> GetKeysFor(int index, Hash256 topic, int from, int to, bool includeValues = false);

    string GetDbSize();

    Task FirstBlockAdded { get; }
    Task CheckMigratedData();

    LogIndexAggregate Aggregate(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null);
    Task SetReceiptsAsync(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null);
    Task SetReceiptsAsync(LogIndexAggregate aggregate, bool isBackwardSync, LogIndexUpdateStats? stats = null);
    Task ReorgFrom(BlockReceipts block);
    Task CompactAsync(bool flush, LogIndexUpdateStats? stats = null);
    Task RecompactAsync(int maxUncompressedLength = -1, LogIndexUpdateStats? stats = null);
}
