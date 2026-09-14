// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
    /// <inheritdoc/>
    /// <exception cref="ArgumentException">Block range exceeds <see cref="IReceiptConfig.MaxBlockDepth"/>.</exception>
    public IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default)
    {
        (BlockHeader fromBlock, BlockHeader toBlock) = LogFinder.ResolveRange(blockFinder, filter, cancellationToken);
        return FindLogs(filter, fromBlock, toBlock, cancellationToken);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">Block range exceeds <see cref="IReceiptConfig.MaxBlockDepth"/>.</exception>
    public IEnumerable<FilterLog> FindLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default)
    {
        EnsureBlockRangeWithinLimit(fromBlock, toBlock);
        return logFinder.FindLogs(filter, fromBlock, toBlock, cancellationToken);
    }

    private void EnsureBlockRangeWithinLimit(BlockHeader fromBlock, BlockHeader toBlock)
    {
        // Read per call, as the module this replaced did, so the limit is not pinned to whenever the
        // singleton happened to be built - which is now the first logs request rather than startup.
        int maxBlockDepth = receiptConfig.MaxBlockDepth;
        if (maxBlockDepth <= 0 || toBlock.Number < fromBlock.Number)
            return;

        ulong rangeSize = toBlock.Number - fromBlock.Number + 1;
        if (rangeSize > (ulong)maxBlockDepth)
        {
            throw new ArgumentException(
                $"Block range {rangeSize} exceeds the maximum of {maxBlockDepth} blocks per logs request. " +
                $"Use a narrower fromBlock/toBlock range or increase Receipt.{nameof(IReceiptConfig.MaxBlockDepth)}.");
        }
    }
}
