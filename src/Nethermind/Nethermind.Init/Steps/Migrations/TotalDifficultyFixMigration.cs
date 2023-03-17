// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Repositories;

namespace Nethermind.Init.Steps.Migrations;

public class TotalDifficultyFixMigration : IDatabaseMigration
{
    private readonly IApiWithNetwork _api;
    private readonly ILogger _logger;
    private readonly ISyncConfig _syncConfig;

    private Task? _fixTask;
    private CancellationTokenSource? _cancellationTokenSource;

    public TotalDifficultyFixMigration(IApiWithNetwork api, ISyncConfig syncConfig)
    {
        _api = api;
        _logger = _api.LogManager.GetClassLogger();
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
        IChainLevelInfoRepository chainLevelInfoRepository = _api.ChainLevelInfoRepository ?? throw new ArgumentNullException(nameof(_api.ChainLevelInfoRepository));
        IBlockTree blockTree = _api.BlockTree ?? throw new ArgumentNullException(nameof(_api.BlockTree));

        lastBlock ??= blockTree.BestKnownNumber;

        if (_logger.IsInfo) _logger.Info($"Starting TotalDifficultyFixMigration. From block {startingBlock} to block {lastBlock}");

        UInt256 previousTd = chainLevelInfoRepository.LoadLevel(startingBlock - 1)!.BlockInfos[0].TotalDifficulty;

        for (long blockNumber = startingBlock; blockNumber <= lastBlock; ++blockNumber)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BlockHeader header = blockTree.FindHeader(blockNumber)!;

            UInt256 expectedTd = previousTd + header.Difficulty;
            ChainLevelInfo currentLevel = chainLevelInfoRepository.LoadLevel(blockNumber)!;
            UInt256 actualTd = currentLevel.BlockInfos[0].TotalDifficulty;
            if (actualTd != expectedTd)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Found discrepancy in block's total difficulty: should be {expectedTd}, was {actualTd}. Fixing.");
                currentLevel.BlockInfos[0].TotalDifficulty = expectedTd;
                chainLevelInfoRepository.PersistLevel(blockNumber, currentLevel);
            }

            previousTd = expectedTd;
        }

        if (_logger.IsInfo) _logger.Info("Ended TotalDifficultyFixMigration.");
    }
}
