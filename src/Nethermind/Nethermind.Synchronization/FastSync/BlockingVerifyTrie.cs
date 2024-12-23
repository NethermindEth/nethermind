// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Synchronization.FastSync;

public class BlockingVerifyTrie(
    IBlockProcessingQueue processingQueue,
    IStateReader stateReader,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IProcessExitSource exitSource,
    ILogManager logManager) : IBlockingVerifyTrie
{
    private readonly ILogger _logger = logManager.GetClassLogger<VerifyStateOnStateSyncFinished>();

    private bool _alreadyRunning = false;

    public bool TryStartVerifyTrie(Hash256 rootNode)
    {
        if (Interlocked.CompareExchange(ref _alreadyRunning, true, false))
        {
            return false;
        }

        ManualResetEvent processingBlocker = new ManualResetEvent(false);

        processingQueue.BlockRemoved += ProcessingQueueOnBlockRemoved;

        Task.Factory.StartNew(() =>
        {
            try
            {
                _logger!.Info("Collecting trie stats and verifying that no nodes are missing...");
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
            finally
            {
                processingBlocker.Set();
                processingQueue.BlockRemoved -= ProcessingQueueOnBlockRemoved;
            }

        }, TaskCreationOptions.LongRunning);

        return true;

        void ProcessingQueueOnBlockRemoved(object? o, BlockRemovedEventArgs blockRemovedEventArgs)
        {
            processingBlocker.WaitOne();
        }
    }
}
