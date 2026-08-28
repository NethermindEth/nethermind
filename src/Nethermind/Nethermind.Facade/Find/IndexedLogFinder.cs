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
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.Logging;
using Autofac.Features.AttributeFilters;

namespace Nethermind.Facade.Find;

/// <summary>
/// Extended <see cref="LogFinder"/> that adds log index support for faster eth_getLogs queries.
/// When the log index is available and applicable, it uses the index to identify relevant blocks
/// before fetching logs from those specific blocks.
/// </summary>
public class IndexedLogFinder(
    IBlockFinder blockFinder,
    [KeyFilter(IReceiptFinder.RegenerableKey)] IReceiptFinder receiptFinder,
    IReceiptStorage receiptStorage,
    ILogManager logManager,
    IReceiptsRecovery receiptsRecovery,
    ILogIndexStorage logIndexStorage,
    int minBlocksToUseIndex = 32,
    IReceiptConfig? receiptConfig = null,
    IPrunedLogsRetention? prunedLogsRetention = null,
    IFlatDbConfig? flatDbConfig = null)
    : LogFinder(blockFinder, receiptFinder, receiptStorage, logManager, receiptsRecovery, receiptConfig, prunedLogsRetention)
{
    private readonly ILogIndexStorage _logIndexStorage = logIndexStorage ?? throw new ArgumentNullException(nameof(logIndexStorage));
    // Direct parameter use in a method body is CS9107 for parameters that also flow to the base constructor,
    // so the boundary guard reads these initializer-copied members instead.
    private readonly IBlockFinder _blockFinder = blockFinder;
    private readonly bool _servesRetainedSlices = prunedLogsRetention is not null && SliceScopeConfig.Parse(flatDbConfig?.HistorySliceAddresses).Count != 0;

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

        // Below the pruned boundary the index holds only the receipt islands slice retention kept, and those
        // blocks also carry other addresses' logs - complete for the sliced addresses, a partial answer for any
        // other filter. A filter the retention does not cover starts at the boundary instead, so its below-boundary
        // prefix falls back to the endpoint probe and fails closed rather than answering short.
        if (_servesRetainedSlices)
        {
            ulong lowestStored = _blockFinder.GetLowestBlock();
            if ((ulong)range.from < lowestStored && !RetainsLogsForFilter(filter, fromBlock.Number, toBlock.Number))
                range.from = (int)lowestStored;
        }

        if (range.from > range.to)
            return null;

        if (range.to - range.from + 1 < minBlocksToUseIndex)
            return null;

        filter.UseIndex = true;
        return range;
    }
}
