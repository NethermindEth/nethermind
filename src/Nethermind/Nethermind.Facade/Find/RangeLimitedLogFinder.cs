// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Filters;

namespace Nethermind.Facade.Find;

/// <summary>
/// Rejects logs queries that would read receipts of more than <see cref="IReceiptConfig.MaxBlockDepth"/>
/// blocks one by one.
/// </summary>
/// <remarks>
/// Only the part of the requested range that the log index cannot answer counts towards the limit, so an
/// indexed node keeps serving wide queries while every sequential fallback of <see cref="IndexedLogFinder"/>
/// stays bounded: a caller opting out with <c>useIndex: false</c>, a filter with neither an address nor a
/// topic, and a range reaching beyond the indexed blocks.
/// </remarks>
public sealed class RangeLimitedLogFinder(
    ILogFinder logFinder,
    IBlockFinder blockFinder,
    IReceiptConfig receiptConfig,
    ILogIndexStorage logIndexStorage) : IRpcLogFinder
{
    /// <inheritdoc/>
    /// <exception cref="ArgumentException">Scan exceeds <see cref="IReceiptConfig.MaxBlockDepth"/> blocks.</exception>
    public IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default)
    {
        (BlockHeader fromBlock, BlockHeader toBlock) = LogFinder.ResolveRange(blockFinder, filter, cancellationToken);
        return FindLogs(filter, fromBlock, toBlock, cancellationToken);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">Scan exceeds <see cref="IReceiptConfig.MaxBlockDepth"/> blocks.</exception>
    public IEnumerable<FilterLog> FindLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default)
    {
        EnsureScanWithinLimit(filter, fromBlock, toBlock);
        return logFinder.FindLogs(filter, fromBlock, toBlock, cancellationToken);
    }

    private void EnsureScanWithinLimit(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock)
    {
        // Read per call, as the module this replaced did, so the limit is not pinned to whenever the
        // singleton happened to be built - which is now the first logs request rather than startup.
        int maxBlockDepth = receiptConfig.MaxBlockDepth;
        if (maxBlockDepth <= 0 || toBlock.Number < fromBlock.Number)
            return;

        ulong scannedBlocks = CountBlocksScannedSequentially(filter, fromBlock.Number, toBlock.Number);
        if (scannedBlocks > (ulong)maxBlockDepth)
        {
            throw new ArgumentException(
                $"Logs request reads {scannedBlocks} blocks without the log index, over the maximum of {maxBlockDepth} per request. " +
                $"Use a narrower fromBlock/toBlock range or increase Receipt.{nameof(IReceiptConfig.MaxBlockDepth)}.");
        }
    }

    /// <remarks>
    /// Deliberately a loose over-estimate of what <see cref="IndexedLogFinder"/> takes from the index. The
    /// conditions it additionally applies - a covered span below its minimum, and the genesis carve-out -
    /// only ever hand a bounded handful of blocks back to the sequential scan, and its retention check
    /// throws instead of scanning. Mirroring those would couple the limit to that finder's internals for
    /// nothing.
    /// </remarks>
    private ulong CountBlocksScannedSequentially(LogFilter filter, ulong fromBlock, ulong toBlock)
    {
        ulong requested = toBlock - fromBlock + 1;

        if (!filter.UseIndex || filter.AcceptsAnyBlock
            || logIndexStorage.MinBlockNumber is not { } indexFrom
            || logIndexStorage.MaxBlockNumber is not { } indexTo)
        {
            return requested;
        }

        ulong coveredFrom = Math.Max(fromBlock, (ulong)indexFrom);
        ulong coveredTo = Math.Min(toBlock, (ulong)indexTo);
        return coveredFrom > coveredTo ? requested : requested - (coveredTo - coveredFrom + 1);
    }
}
