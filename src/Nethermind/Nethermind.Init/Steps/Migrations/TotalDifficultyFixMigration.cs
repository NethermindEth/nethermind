// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Repositories;

namespace Nethermind.Init.Steps.Migrations;

public class TotalDifficultyFixMigration : IDatabaseMigration
{
    private readonly ILogger _logger;
    private readonly ISyncConfig _syncConfig;
    private readonly IChainLevelInfoRepository _chainLevelInfoRepository;
    private readonly IBlockTree _blockTree;
    private readonly ProgressLogger _progressLogger;

    public TotalDifficultyFixMigration(IChainLevelInfoRepository? chainLevelInfoRepository, IBlockTree? blockTree, ISyncConfig syncConfig, ILogManager logManager)
    {
        _chainLevelInfoRepository = chainLevelInfoRepository ?? throw new ArgumentNullException(nameof(chainLevelInfoRepository));
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _logger = logManager.GetClassLogger();
        _syncConfig = syncConfig;
        _progressLogger = new ProgressLogger("TotalDifficulty Fix", logManager);
    }

    public Task Run(CancellationToken cancellationToken)
    {
        if (_syncConfig.FixTotalDifficulty)
        {
            try
            {
                RunMigration(_syncConfig.FixTotalDifficultyStartingBlock, _syncConfig.FixTotalDifficultyLastBlock, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.Error($"Failed to finish TotalDifficultyFixMigration {e.Message}");
            }
        }

        return Task.CompletedTask;
    }

    private void RunMigration(long startingBlock, long? lastBlock, CancellationToken cancellationToken)
    {
        lastBlock ??= _blockTree.BestKnownNumber;

        if (_logger.IsInfo) _logger.Info($"Starting TotalDifficultyFixMigration. From block {startingBlock} to block {lastBlock}");

        long totalBlocks = lastBlock.Value - startingBlock + 1;
        long processedBlocks = 0;
        long fixedBlocks = 0;

        _progressLogger.Reset(0, totalBlocks);

        for (long blockNumber = startingBlock; blockNumber <= lastBlock; ++blockNumber)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ChainLevelInfo currentLevel = _chainLevelInfoRepository.LoadLevel(blockNumber)!;

            bool shouldPersist = false;
            foreach (BlockInfo blockInfo in currentLevel.BlockInfos)
            {
                BlockHeader header = _blockTree.FindHeader(blockInfo.BlockHash)!;
                UInt256? parentTd = FindParentTd(header, blockNumber);

                if (parentTd is null) continue;

                UInt256 expectedTd = parentTd.Value + header.Difficulty;
                UInt256 actualTd = blockInfo.TotalDifficulty;
                if (actualTd != expectedTd)
                {
                    if (_logger.IsWarn)
                        _logger.Warn(
                            $"Found discrepancy in block {header.ToString(BlockHeader.Format.Short)} total difficulty: should be {expectedTd}, was {actualTd}. Fixing.");
                    blockInfo.TotalDifficulty = expectedTd;
                    shouldPersist = true;
                    fixedBlocks++;
                }
            }

            if (shouldPersist)
            {
                _chainLevelInfoRepository.PersistLevel(blockNumber, currentLevel);
            }

            processedBlocks++;
            
            // Update progress every 1000 blocks or when progress logger suggests
            if (processedBlocks % 1000 == 0)
            {
                _progressLogger.Update(processedBlocks);
                _progressLogger.LogProgress();
            }
        }

        // Final progress update
        _progressLogger.Update(processedBlocks);
        _progressLogger.MarkEnd();
        _progressLogger.LogProgress();

        if (_logger.IsInfo) _logger.Info($"Ended TotalDifficultyFixMigration. Processed {processedBlocks} blocks, fixed {fixedBlocks} blocks.");
    }

    UInt256? FindParentTd(BlockHeader blockHeader, long level)
    {
        if (blockHeader.ParentHash is null) return null;
        Hash256? parentHash = _blockTree.FindHeader(blockHeader.ParentHash)?.Hash;
        if (parentHash is null) return null;
        ChainLevelInfo levelInfo = _chainLevelInfoRepository.LoadLevel(level - 1)!;
        foreach (BlockInfo blockInfo in levelInfo.BlockInfos)
        {
            if (parentHash == blockInfo.BlockHash)
            {
                return blockInfo.TotalDifficulty;
            }
        }

        return null;
    }
}
