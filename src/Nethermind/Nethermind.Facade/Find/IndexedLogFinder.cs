// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Facade.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Db.LogIndex;
using Nethermind.Logging;

namespace Nethermind.Facade.Find;

/// <summary>
/// Extended <see cref="LogFinder"/> that adds log index support for faster eth_getLogs queries.
/// When the log index is available and applicable, it uses the index to identify relevant blocks
/// before fetching logs from those specific blocks.
/// </summary>
public class IndexedLogFinder(
    IBlockFinder blockFinder,
    IReceiptFinder receiptFinder,
    IReceiptStorage receiptStorage,
    ILogManager logManager,
    IReceiptsRecovery receiptsRecovery,
    ILogIndexStorage logIndexStorage,
    int maxBlockDepth = 1000,
    int minBlocksToUseIndex = 32)
    : LogFinder(blockFinder, receiptFinder, receiptStorage, logManager, receiptsRecovery, maxBlockDepth)
{
    private readonly ILogIndexStorage _logIndexStorage = logIndexStorage ?? throw new ArgumentNullException(nameof(logIndexStorage));

    public override IEnumerable<FilterLog> FindLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default) =>
        GetLogIndexRange(filter, fromBlock, toBlock) is not { } indexRange
            ? base.FindLogs(filter, fromBlock, toBlock, cancellationToken)
            : FindIndexedLogs(filter, fromBlock, toBlock, indexRange, cancellationToken);

    private IEnumerable<FilterLog> FindIndexedLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, (int from, int to) indexRange, CancellationToken cancellationToken)
    {
        if ((ulong)indexRange.from > fromBlock.Number && FindHeaderOrLogError((ulong)(indexRange.from - 1), cancellationToken) is { } beforeIndex)
        {
            foreach (FilterLog log in base.FindLogs(filter, fromBlock, beforeIndex, cancellationToken))
                yield return log;
        }

        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<ulong> indexBlockNumbers = _logIndexStorage
            .EnumerateBlockNumbersFor(filter, (ulong)indexRange.from, (ulong)indexRange.to)
            .Select(static n => (ulong)n);

        foreach (FilterLog log in FilterLogsInBlocksParallel(filter, indexBlockNumbers, cancellationToken: cancellationToken))
        {
            yield return log;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if ((ulong)indexRange.to < toBlock.Number && FindHeaderOrLogError((ulong)(indexRange.to + 1), cancellationToken) is { } afterIndex)
        {
            foreach (FilterLog log in base.FindLogs(filter, afterIndex, toBlock, cancellationToken))
                yield return log;
        }
    }

    private (int from, int to)? GetLogIndexRange(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock)
    {
        bool tryUseIndex = filter.UseIndex;
        filter.UseIndex = false;

        if (!tryUseIndex || !_logIndexStorage.Enabled || filter.AcceptsAnyBlock)
            return null;

        if (_logIndexStorage.MinBlockNumber is not { } indexFrom || _logIndexStorage.MaxBlockNumber is not { } indexTo)
            return null;

        (int from, int to) range = (
            Math.Max((int)fromBlock.Number, indexFrom),
            Math.Min((int)toBlock.Number, indexTo)
        );

        if (range.from > range.to)
            return null;

        if (range.to - range.from + 1 < minBlocksToUseIndex)
            return null;

        filter.UseIndex = true;
        return range;
    }
}
