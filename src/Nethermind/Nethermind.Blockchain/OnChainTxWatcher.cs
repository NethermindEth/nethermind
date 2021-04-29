//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Blockchain
{
    public class OnChainTxWatcher : IDisposable
    {
        private readonly IBlockTree _blockTree;
        private readonly ITxPool _txPool;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;

        public OnChainTxWatcher(IBlockTree blockTree, ITxPool txPool, ISpecProvider? specProvider, ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree.BlockAddedToMain += OnBlockAddedToMain;
        }

        private void OnBlockAddedToMain(object sender, BlockReplacementEventArgs e)
        {
            // we don't want this to be on main processing thread
            Task.Run(() => ProcessBlock(e.Block, e.PreviousBlock))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error($"Couldn't correctly add or remove transactions from txpool after processing block {e.Block.ToString(Block.Format.FullHashAndNumber)}.", t.Exception);
                    }
                });
        }

        private void ProcessBlock(Block block, Block? previousBlock)
        {
            _txPool.BlockGasLimit = block.GasLimit;
            long transactionsInBlock = block.Transactions.Length;
            long discoveredForPendingTxs = 0;
            long discoveredForHashCache = 0;

            StringBuilder penSn = new();
            StringBuilder txsNotInHc = new();
            StringBuilder txsNotInPendingButInHc = new();
            foreach (Transaction tx in _txPool.GetPendingTransactions())
            {
                penSn.Append(GetTxsDetails(tx));
            }

            for (int i = 0; i < transactionsInBlock; i++)
            {
                Keccak txHash = block.Transactions[i].Hash;
                if (!_txPool.IsInHashCache(txHash))
                {
                    discoveredForHashCache++;
                    txsNotInHc.Append(GetTxsDetails(block.Transactions[i]));
                }

                if (!_txPool.RemoveTransaction(block.Transactions[i], true))
                {
                    discoveredForPendingTxs++;
                    
                    if (_txPool.IsInHashCache(txHash))
                    {
                        txsNotInPendingButInHc.Append(GetTxsDetails(block.Transactions[i]));
                    }
                }
            }

            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "txinvestigation");
            string fileNameNotHc = $"{dir}/{block.Number}notInHC.csv";
            File.WriteAllText(fileNameNotHc, txsNotInHc.ToString());
            
            string fileNameNotInPendingButInHc = $"{dir}/{block.Number}notInPendingButInHS.csv";
            File.WriteAllText(fileNameNotInPendingButInHc, txsNotInPendingButInHc.ToString());
            
            string fileNamePenSn = $"{dir}/{block.Number}penSn.csv";
            File.WriteAllText(fileNamePenSn, penSn.ToString());

            TxPool.Metrics.DarkPoolRatioLevel1 = transactionsInBlock == 0 ? 0 : (float)discoveredForHashCache / transactionsInBlock;
            TxPool.Metrics.DarkPoolRatioLevel2 = transactionsInBlock == 0 ? 0 : (float)discoveredForPendingTxs / transactionsInBlock;
            
            // the hash will only be the same during perf test runs / modified DB states
            if (previousBlock is not null)
            {
                bool isEip155Enabled = _specProvider.GetSpec(previousBlock.Number).IsEip155Enabled;
                for (int i = 0; i < previousBlock.Transactions.Length; i++)
                {
                    Transaction tx = previousBlock.Transactions[i];
                    _txPool.AddTransaction(tx, (isEip155Enabled ? TxHandlingOptions.None : TxHandlingOptions.PreEip155Signing) | TxHandlingOptions.Reorganisation);
                }
            }
        }

        private string GetTxsDetails(Transaction tx)
        {
            Address senderAddress = tx.SenderAddress;
            UInt256 currentNonce = _txPool.GetNonce(senderAddress);
            UInt256 txNonce = tx.Nonce;
            UInt256 gasPrice = tx.GasPrice/1000000000;
            UInt256 gasBottleneck = tx.GasBottleneck / 1000000000;
            long nonceDiff = (long)txNonce - (long)currentNonce;
            
            StringBuilder details = new();
            
            details.Append(tx.Hash);
            details.Append(',');
            details.Append(senderAddress);
            details.Append(',');
            details.Append(gasPrice);
            details.Append(',');
            details.Append(gasBottleneck);
            details.Append(',');
            details.Append(currentNonce);
            details.Append(',');
            details.Append(txNonce);
            details.Append(',');
            details.Append(nonceDiff);
            details.Append(',');
            details.Append(tx.Timestamp);
            details.AppendLine();

            return details.ToString();
        }

        public void Dispose()
        {
            _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
        }
    }
}
