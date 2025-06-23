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
    int GetLastKnownBlockNumber();
    IEnumerable<int> GetBlockNumbersFor(Address address, int from, int to);

    IEnumerable<int> GetBlockNumbersFor(Hash256 topic, int from, int to);
    Task CheckMigratedData();
    Task<SetReceiptsStats> SetReceiptsAsync(int blockNumber, TxReceipt[] receipts, bool isBackwardSync);
    Task<SetReceiptsStats> SetReceiptsAsync(BlockReceipts[] batch, bool isBackwardSync);
    Task ReorgFrom(BlockReceipts block);
    SetReceiptsStats Compact(bool waitForCompression);
    SetReceiptsStats Recompact(int maxUncompressedLength = -1);

    PagesStats PagesStats => default;
    string TempFilePath => "";
    string FinalFilePath => "";
}
