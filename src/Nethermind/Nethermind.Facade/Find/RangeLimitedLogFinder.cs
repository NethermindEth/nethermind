// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Facade.Filters;

namespace Nethermind.Facade.Find;

/// <summary>
/// Rejects logs queries whose block range exceeds <see cref="IReceiptConfig.MaxBlockDepth"/>.
/// </summary>
public sealed class RangeLimitedLogFinder(ILogFinder logFinder, IBlockFinder blockFinder, IReceiptConfig receiptConfig) : IRpcLogFinder
{
    private readonly int _maxBlockDepth = receiptConfig.MaxBlockDepth;

    public IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default)
    {
        (BlockHeader fromBlock, BlockHeader toBlock) = LogFinder.ResolveRange(blockFinder, filter, cancellationToken);
        return FindLogs(filter, fromBlock, toBlock, cancellationToken);
    }

    public IEnumerable<FilterLog> FindLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default)
    {
        EnsureBlockRangeWithinLimit(fromBlock, toBlock);
        return logFinder.FindLogs(filter, fromBlock, toBlock, cancellationToken);
    }

    private void EnsureBlockRangeWithinLimit(BlockHeader fromBlock, BlockHeader toBlock)
    {
        if (_maxBlockDepth <= 0 || toBlock.Number < fromBlock.Number)
            return;

        ulong rangeSize = toBlock.Number - fromBlock.Number + 1;
        if (rangeSize > (ulong)_maxBlockDepth)
        {
            throw new ArgumentException(
                $"Block range {rangeSize} exceeds the maximum of {_maxBlockDepth} blocks per logs request. " +
                $"Use a narrower fromBlock/toBlock range or increase Receipt.{nameof(IReceiptConfig.MaxBlockDepth)}.");
        }
    }
}
