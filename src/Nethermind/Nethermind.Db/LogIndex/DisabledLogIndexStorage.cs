// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Db;

public sealed class DisabledLogIndexStorage : ILogIndexStorage
{
    public bool Enabled => false;

    public Task FirstBlockAdded => Task.CompletedTask;

    public string GetDbSize() => "0 B";

    public int? GetMaxBlockNumber() => null;

    public int? GetMinBlockNumber() => null;

    public List<int> GetBlockNumbersFor(Address address, int from, int to)
    {
        throw new NotSupportedException();
    }

    public List<int> GetBlockNumbersFor(int index, Hash256 topic, int from, int to)
    {
        throw new NotSupportedException();
    }

    public Dictionary<byte[], int[]> GetKeysFor(Address address, int from, int to, bool includeValues = false)
    {
        throw new NotSupportedException();
    }

    public Dictionary<byte[], int[]> GetKeysFor(int index, Hash256 topic, int from, int to, bool includeValues = false)
    {
        throw new NotSupportedException();
    }

    public Task CheckMigratedData() => Task.CompletedTask;

    public LogIndexAggregate Aggregate(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null)
    {
        throw new NotSupportedException();
    }

    public Task SetReceiptsAsync(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null)
    {
        throw new NotSupportedException();
    }

    public Task SetReceiptsAsync(LogIndexAggregate aggregate, LogIndexUpdateStats? stats = null)
    {
        throw new NotSupportedException();
    }

    public Task ReorgFrom(BlockReceipts block)
    {
        throw new NotSupportedException();
    }

    public Task CompactAsync(bool flush = false, int mergeIterations = 0, LogIndexUpdateStats? stats = null)
    {
        throw new NotSupportedException();
    }

    public Task RecompactAsync(int maxUncompressedLength = -1, LogIndexUpdateStats? stats = null)
    {
        throw new NotSupportedException();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}
