// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Autofac;
using Autofac.Features.AttributeFilters;
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
    ILogManager logManager) : IStartable
{
    private readonly ILogger _logger = logManager.GetClassLogger<VerifyStateOnStateSyncFinished>();

    public void Start()
    {
        treeSync.OnVerifyPostSyncCleanup += TreeSyncOnOnVerifyPostSyncCleanup;
    }

    private void TreeSyncOnOnVerifyPostSyncCleanup(object? sender, ITreeSync.VerifyPostSyncCleanupEventArgs e)
    {
        ManualResetEvent processingBlocker = new ManualResetEvent(false);

        processingQueue.BlockRemoved += ProcessingQueueOnBlockRemoved;

        try
        {
            Hash256 rootNode = e.Root;
            _logger!.Info("Collecting trie stats and verifying that no nodes are missing...");
            TrieStats stats = stateReader.CollectStats(rootNode, codeDb, logManager);
            _logger.Info($"Stats after finishing state" + stats);
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