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
    IPrunedLogsRetention? prunedLogsRetention = null)
    : LogFinder(blockFinder, receiptFinder, receiptStorage, logManager, receiptsRecovery, receiptConfig, prunedLogsRetention)
{
    private readonly ILogIndexStorage _logIndexStorage = logIndexStorage ?? throw new ArgumentNullException(nameof(logIndexStorage));
    // CS9107: a primary-ctor parameter that also flows to the base ctor cannot be used in a method body.
    private readonly IBlockFinder _blockFinder = blockFinder;

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

        // Rejected eagerly once the index is in play: the endpoint probe only sees the two endpoint headers,
        // so it cannot notice reclaimed receipts in the interior. Keyed on the query, not the index range - an
        // index starting exactly at the boundary would otherwise route the doomed prefix to that probe.
        // Genesis is the one below-boundary prefix with nothing to lose. Only address-filtered queries are
        // held to this: retention can never vouch for a topic-only filter, so it is answered from the index
        // and its below-boundary part may be short over reclaimed receipts - the same answer master gives.
        ulong lowestStored = _blockFinder.GetLowestBlock();
        bool uncoveredBelowBoundary = fromBlock.Number < lowestStored
            && filter.AddressFilter.Addresses.Count != 0
            && !RetainsLogsForFilter(filter, fromBlock.Number, toBlock.Number);
        if (uncoveredBelowBoundary && (fromBlock.Number != 0 || lowestStored != 1))
        {
            filter.UseIndex = tryUseIndex;
            throw new ResourceNotFoundException($"Receipt not available for From block {fromBlock.Number}.");
        }

        (int from, int to) range = (
            Math.Max((int)fromBlock.Number, indexFrom),
            Math.Min((int)toBlock.Number, indexTo)
        );

        // Only the genesis carve-out reaches here uncovered; lift block 0 out of the index range.
        if (uncoveredBelowBoundary && range.from == 0)
            range.from = 1;

        if (range.from > range.to)
            return null;

        if (range.to - range.from + 1 < minBlocksToUseIndex)
            return null;

        filter.UseIndex = true;
        return range;
    }
}
