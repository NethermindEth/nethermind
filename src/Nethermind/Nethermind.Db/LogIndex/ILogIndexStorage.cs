// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Db;

public readonly record struct BlockReceipts(int BlockNumber, TxReceipt[] Receipts);

public interface ILogIndexStorage : IAsyncDisposable, IStoppableService
{
    int? GetMaxBlockNumber();
    int? GetMinBlockNumber();
    IEnumerable<int> GetBlockNumbersFor(Address address, int from, int to);

    IEnumerable<int> GetBlockNumbersFor(Hash256 topic, int from, int to);
    Task CheckMigratedData();
    Task<LogIndexUpdateStats> SetReceiptsAsync(int blockNumber, TxReceipt[] receipts, bool isBackwardSync);
    Task<LogIndexUpdateStats> SetReceiptsAsync(BlockReceipts[] batch, bool isBackwardSync);
    Task ReorgFrom(BlockReceipts block);
    Task<CompactingStats> CompactAsync(bool flush);
    Task<LogIndexUpdateStats> RecompactAsync(int maxUncompressedLength = -1);
}
