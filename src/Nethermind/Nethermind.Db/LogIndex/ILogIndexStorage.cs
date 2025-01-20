// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Db;

public readonly record struct BlockReceipts(int BlockNumber, TxReceipt[] Receipts);

public interface ILogIndexStorage : IAsyncDisposable
{
    int GetLastKnownBlockNumber();
    IEnumerable<int> GetBlockNumbersFor(Address address, int from, int to);

    IEnumerable<int> GetBlockNumbersFor(Hash256 topic, int from, int to);
    Task CheckMigratedData();
    Task<SetReceiptsStats> SetReceiptsAsync(int blockNumber, TxReceipt[] receipts, bool isBackwardSync, CancellationToken cancellationToken);
    Task<SetReceiptsStats> SetReceiptsAsync(BlockReceipts[] batch, bool isBackwardSync, CancellationToken cancellationToken);
    Task StopAsync();

    PagesStats PagesStats { get; }
    string TempFilePath { get; }
    string FinalFilePath { get; }
}
