// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.SkipIndexedBlockInfo;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Init.Steps.Migrations;

public class SkipIndexedBlockInfoMigration(
    IBlockTree blockTree,
    ISkipIndexedBlockInfoStore store,
    [KeyFilter(DbNames.SkipIndexedBlockInfo)] IDb cumulativeBlockInfoDb,
    ISyncModeSelector syncModeSelector,
    ITotalDifficultyAnchor anchor,
    ILogManager logManager) : IDatabaseMigration
{
    private readonly ILogger _logger = logManager.GetClassLogger<SkipIndexedBlockInfoMigration>();

    public async Task Run(CancellationToken cancellationToken)
    {
        if (cumulativeBlockInfoDb.GetAllKeys(ordered: false).Any())
        {
            if (_logger.IsDebug) _logger.Debug("SkipIndexedBlockInfoMigration skipped: database is not empty.");
            return;
        }

        await syncModeSelector.WaitUntilMode(static m => m.NotSyncing(), cancellationToken);

        Block? head = blockTree.Head;
        if (head is null || head.Number <= 0)
        {
            if (_logger.IsInfo) _logger.Info("SkipIndexedBlockInfoMigration skipped: no head block available.");
            return;
        }

        long headNumber = head.Number;
        long floor = anchor.TryGet()?.Number ?? 0;
        if (floor >= headNumber)
        {
            if (_logger.IsInfo) _logger.Info($"SkipIndexedBlockInfoMigration skipped: head {headNumber} at or below anchor {floor}.");
            return;
        }

        long range = headNumber - floor;
        int partitionCount = (int)Math.Min(Environment.ProcessorCount, range);
        long partitionSize = range / partitionCount;

        (long start, long end)[] partitions = new (long, long)[partitionCount];
        for (int i = 0; i < partitionCount; i++)
        {
            long start = floor + i * partitionSize;
            long end = (i == partitionCount - 1) ? headNumber : start + partitionSize;
            partitions[i] = (start, end);
        }

        if (_logger.IsInfo)
            _logger.Info($"Starting SkipIndexedBlockInfoMigration: head={headNumber}, floor={floor}, partitions={partitionCount}, partitionSize~{partitionSize}.");

        try
        {
            Parallel.ForEach(
                partitions,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = partitionCount,
                    CancellationToken = cancellationToken
                },
                PopulatePartition);
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsInfo) _logger.Info("SkipIndexedBlockInfoMigration cancelled.");
            return;
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("SkipIndexedBlockInfoMigration failed.", e);
            return;
        }

        if (_logger.IsInfo) _logger.Info("SkipIndexedBlockInfoMigration finished.");
    }

    private void PopulatePartition((long start, long end) partition)
    {
        BlockHeader? endHeader = blockTree.FindHeader(partition.end);
        if (endHeader?.Hash is null)
        {
            if (_logger.IsWarn)
                _logger.Warn($"SkipIndexedBlockInfoMigration: missing header at block {partition.end}, skipping partition [{partition.start}, {partition.end}].");
            return;
        }

        ValueHash256 endHash = endHeader.Hash.ValueHash256;
        store.GetAncestorAt(partition.end, in endHash, partition.start);
    }
}
