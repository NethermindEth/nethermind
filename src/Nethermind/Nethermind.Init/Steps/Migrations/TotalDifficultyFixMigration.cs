// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FastEnumUtility;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
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

    private Task? _fixTask;
    private CancellationTokenSource? _cancellationTokenSource;

    public TotalDifficultyFixMigration(IChainLevelInfoRepository? chainLevelInfoRepository, IBlockTree? blockTree, ISyncConfig syncConfig, ILogManager logManager)
    {
        _chainLevelInfoRepository = chainLevelInfoRepository ?? throw new ArgumentNullException(nameof(chainLevelInfoRepository));
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _logger = logManager.GetClassLogger();
        _syncConfig = syncConfig;
    }

    public async ValueTask DisposeAsync()
    {
        _cancellationTokenSource?.Cancel();
        await (_fixTask ?? Task.CompletedTask);
    }

    public void Run()
    {
        if (_syncConfig.FixTotalDifficulty)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = _cancellationTokenSource.Token;

            _fixTask = Task.Run(() => RunMigration(_syncConfig.FixTotalDifficultyStartingBlock, _syncConfig.FixTotalDifficultyLastBlock, token), token)
                .ContinueWith(x =>
                    {
                        if (x.IsFaulted && _logger.IsError)
                        {
                            _logger.Error($"Failed to finish TotalDifficultyFixMigration {x.Exception!.Message}");
                        }
                    });
        }
    }

    private void RunMigration(long startingBlock, long? lastBlock, CancellationToken cancellationToken)
    {
        lastBlock ??= _blockTree.BestKnownNumber;

        if (_logger.IsInfo) _logger.Info($"Starting TotalDifficultyFixMigration. From block {startingBlock} to block {lastBlock}");

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
                }
            }

            if (shouldPersist)
            {
                _chainLevelInfoRepository.PersistLevel(blockNumber, currentLevel);
            }
        }

        if (_logger.IsInfo) _logger.Info("Ended TotalDifficultyFixMigration.");
    }

    UInt256? FindParentTd(BlockHeader blockHeader, long level)
    {
        if (blockHeader.ParentHash is null) return null;
        Keccak? parentHash = _blockTree.FindHeader(blockHeader.ParentHash)?.Hash;
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
