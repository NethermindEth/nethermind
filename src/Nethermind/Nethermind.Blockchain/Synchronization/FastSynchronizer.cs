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
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Logging;

namespace Nethermind.Blockchain.Synchronization
{
    public class FastSynchronizer : IFastSynchronizer
    {
        private readonly ILogger _logger;
        private readonly INodeDataDownloader _nodeDataDownloader;
        private readonly IReceiptStorage _receiptStorage;

        public FastSynchronizer(INodeDataDownloader nodeDataDownloader, IReceiptStorage receiptStorage, ILogManager logManager)
        {
            _nodeDataDownloader = nodeDataDownloader ?? throw new ArgumentNullException(nameof(nodeDataDownloader));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        [Todo(Improve.MissingFunctionality, "Eth63 / fast sync can download receipts using this method. Fast sync is not implemented although its methods and serializers are already written.")]
        private async Task<bool> DownloadReceipts(Block[] blocks, ISyncPeer peer)
        {
            var blocksWithTransactions = blocks.Where(b => b.Transactions.Length != 0).ToArray();
            if (blocksWithTransactions.Length != 0)
            {
                var receiptsTask = peer.GetReceipts(blocksWithTransactions.Select(b => b.Hash).ToArray(), CancellationToken.None);
                var transactionReceipts = await receiptsTask;
                if (receiptsTask.IsCanceled) return true;

                if (receiptsTask.IsFaulted)
                {
                    if (receiptsTask.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                    {
                        if (_logger.IsTrace) _logger.Error("Failed to retrieve receipts when synchronizing (Timeout)", receiptsTask.Exception);
                    }
                    else
                    {
                        if (_logger.IsError) _logger.Error("Failed to retrieve receipts when synchronizing", receiptsTask.Exception);
                    }

                    throw receiptsTask.Exception;
                }

                for (int blockIndex = 0; blockIndex < blocksWithTransactions.Length; blockIndex++)
                {
                    long gasUsedTotal = 0;
                    for (int txIndex = 0; txIndex < blocksWithTransactions[blockIndex].Transactions.Length; txIndex++)
                    {
                        TransactionReceipt transactionReceipt = transactionReceipts[blockIndex][txIndex];
                        if (transactionReceipt == null) throw new DataException($"Missing receipt for {blocksWithTransactions[blockIndex].Hash}->{txIndex}");

                        transactionReceipt.Index = txIndex;
                        transactionReceipt.BlockHash = blocksWithTransactions[blockIndex].Hash;
                        transactionReceipt.BlockNumber = blocksWithTransactions[blockIndex].Number;
                        transactionReceipt.TransactionHash = blocksWithTransactions[blockIndex].Transactions[txIndex].Hash;
                        gasUsedTotal += transactionReceipt.GasUsed;
                        transactionReceipt.GasUsedTotal = gasUsedTotal;
                        transactionReceipt.Recipient = blocksWithTransactions[blockIndex].Transactions[txIndex].To;

                        // only after execution
                        // receipt.Sender = blocksWithTransactions[blockIndex].Transactions[txIndex].SenderAddress; 
                        // receipt.Error = ...
                        // receipt.ContractAddress = ...

                        _receiptStorage.Add(transactionReceipt);
                    }
                }
            }

            return false;
        }
    }
}