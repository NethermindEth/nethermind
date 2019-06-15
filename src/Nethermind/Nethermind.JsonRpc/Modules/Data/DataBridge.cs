/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.PubSub;

namespace Nethermind.JsonRpc.Modules.Data
{
    public class DataBridge : IDataBridge
    {
        private readonly ISubscription _subscription;
        private readonly IBlockTree _blockTree;
        private readonly BlockingCollection<ReplayJob> _jobs = new BlockingCollection<ReplayJob>(new ConcurrentQueue<ReplayJob>());
        private readonly ILogger _logger;
        private readonly IReceiptStorage _receiptStorage;
        private CancellationTokenSource _cancellationSource;
        private ReplayJob _currentJob;

        public DataBridge(ISubscription subscription, IReceiptStorage receiptStorage, IBlockTree blockTree, ILogManager logManager)
        {
            _subscription = subscription ?? throw new ArgumentNullException(nameof(subscription));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        }

        public void ReplayBlocks(long startBlockNumber, long endBlockNumber)
        {
            if (_currentJob != null) _currentJob.IsCancellationRequested = true;

            _currentJob = new ReplayJob(startBlockNumber, endBlockNumber);
            _jobs.Add(_currentJob);
        }

        public void Start()
        {
            _cancellationSource = new CancellationTokenSource();
            Task.Factory.StartNew(KeepProcessingJobs, _cancellationSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task StopAsync()
        {
            _cancellationSource?.Cancel();
            await Task.CompletedTask;
        }

        private void KeepProcessingJobs()
        {
            foreach (ReplayJob job in _jobs.GetConsumingEnumerable(_cancellationSource.Token))
            {
                if (_cancellationSource.IsCancellationRequested) break;

                if (job.IsCancellationRequested) continue;

                for (long i = job.StartBlock; i < job.EndBlock; i++)
                {
                    if (job.IsCancellationRequested || _cancellationSource.IsCancellationRequested) break;

                    Block block = _blockTree.FindBlock(i);
                    _subscription.PublishBlockAsync(block);

                    for (int txIndex = 0; txIndex < block.Transactions.Length; txIndex++)
                    {
                        if (job.IsCancellationRequested) break;

                        TxReceipt receipt = _receiptStorage.Find(block.Transactions[txIndex].Hash);
                        FullTransaction fullTx = new FullTransaction(txIndex, block.Transactions[txIndex], receipt);
                        _subscription.PublishTransactionAsync(fullTx);
                    }
                }
            }
        }

        private class ReplayJob
        {
            public ReplayJob(long startBlock, long endBlock)
            {
                StartBlock = startBlock;
                EndBlock = endBlock;
            }

            public long StartBlock { get; }
            public long EndBlock { get; }

            public bool IsCancellationRequested { get; set; }
        }
    }
}