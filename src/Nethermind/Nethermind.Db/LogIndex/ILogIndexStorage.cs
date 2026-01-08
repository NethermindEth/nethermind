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

    int? MaxBlockNumber { get; }
    int? MinBlockNumber { get; }

    IEnumerator<int> GetEnumerator(Address address, int from, int to);
    IEnumerator<int> GetEnumerator(int index, Hash256 topic, int from, int to);

    string GetDbSize();

    LogIndexAggregate Aggregate(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null);
    Task SetReceiptsAsync(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null);
    Task SetReceiptsAsync(LogIndexAggregate aggregate, LogIndexUpdateStats? stats = null);
    Task ReorgFrom(BlockReceipts block);
    Task CompactAsync(bool flush = false, int mergeIterations = 0, LogIndexUpdateStats? stats = null);
}
