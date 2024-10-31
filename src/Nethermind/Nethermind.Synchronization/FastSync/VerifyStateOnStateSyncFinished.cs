// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Synchronization.FastSync;

public class VerifyStateOnStateSyncFinished(
    IBlockProcessingQueue processingQueue,
    ITreeSync treeSync,
    IStateReader stateReader,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IProcessExitSource exitSource,
    ILogManager logManager) : IStartable
{
    private readonly ILogger _logger = logManager.GetClassLogger<VerifyStateOnStateSyncFinished>();

    public void Start()
    {
        treeSync.SyncCompleted += TreeSyncOnOnVerifyPostSyncCleanup;
    }

    private void TreeSyncOnOnVerifyPostSyncCleanup(object? sender, ITreeSync.SyncCompletedEventArgs evt)
    {
        ManualResetEvent processingBlocker = new ManualResetEvent(false);

        processingQueue.BlockRemoved += ProcessingQueueOnBlockRemoved;

        try
        {
            Hash256 rootNode = evt.Root;
            _logger!.Info("Collecting trie stats and verifying that no nodes are missing...");
            TrieStats stats = stateReader.CollectStats(rootNode, codeDb, logManager, exitSource.Token);
            if (stats.MissingNodes > 0)
            {
                _logger.Error($"Missing node found!");
            }
            _logger.Info($"Stats after finishing state" + stats);
        }
        catch (Exception e)
        {
            _logger.Error($"Error in verify trie", e);
        }
        finally
        {
            processingBlocker.Set();
            processingQueue.BlockRemoved -= ProcessingQueueOnBlockRemoved;
        }

        return;

        void ProcessingQueueOnBlockRemoved(object? o, BlockRemovedEventArgs blockRemovedEventArgs)
        {
            processingBlocker.WaitOne();
        }
    }
}
