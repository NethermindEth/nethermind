// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Db;

public interface ILogIndexStorage : IDisposable
{
    int GetLastKnownBlockNumber();
    IEnumerable<int> GetBlockNumbersFor(Address address, int from, int to);

    IEnumerable<int> GetBlockNumbersFor(Hash256 topic, int from, int to);
    int SetReceipts(int blockNumber, TxReceipt[] receipts, bool isBackwardSync, CancellationToken cancellationToken);
    int SetReceipts(ReadOnlySpan<(int blockNumber, TxReceipt[] receipts)> batch, bool isBackwardSync, CancellationToken cancellationToken);

    ExecTimeStats SeekForPrevHitStats { get; set; }
    ExecTimeStats SeekForPrevMissStats { get; set; }
}
