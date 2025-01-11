// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.FastSync;

internal class BlockingVerifyTrie(
    ITrieStore trieStore,
    IStateReader stateReader,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IProcessExitSource exitSource,
    ILogManager logManager) : IBlockingVerifyTrie
{
    private readonly ILogger _logger = logManager.GetClassLogger<BlockingVerifyTrie>();

    private bool _alreadyRunning = false;

    public bool TryStartVerifyTrie(BlockHeader stateAtBlock)
    {
        if (Interlocked.CompareExchange(ref _alreadyRunning, true, false))
        {
            return false;
        }

        Task.Factory.StartNew(() =>
        {
            try
            {
                _logger!.Info("Collecting trie stats and verifying that no nodes are missing...");

                Hash256 rootNode = stateAtBlock.StateRoot;

                // This is to block processing as with halfpath old nodes will be removed
                using IBlockCommitter? _ = trieStore.BeginBlockCommit(stateAtBlock.Number + 1);

                TrieStats stats = stateReader.CollectStats(rootNode, codeDb, logManager, exitSource.Token);
                if (stats.MissingNodes > 0)
                {
                    _logger.Error($"Missing node found!");
                }

                _logger.Info($"Stats after finishing state \n" + stats);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _logger.Error($"Error in verify trie", e);
            }

        }, TaskCreationOptions.LongRunning);

        return true;
    }

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        // This is to block processing as with halfpath old nodes will be removed
        using IBlockCommitter? _ = trieStore.BeginBlockCommit(stateAtBlock.Number + 1);

        Hash256 rootNode = stateAtBlock.StateRoot;
        TrieStats stats = stateReader.CollectStats(rootNode, codeDb, logManager, cancellationToken);
        if (stats.MissingNodes > 0)
        {
            _logger.Error($"Missing node found!");
        }

        _logger.Info($"Stats after finishing state \n" + stats);

        return stats.MissingNodes == 0;
    }
}
