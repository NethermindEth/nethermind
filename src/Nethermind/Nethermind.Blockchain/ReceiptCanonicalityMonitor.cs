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
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Blockchain
{
    public class ReceiptCanonicalityMonitor : IDisposable
    {
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ILogger _logger;

        public ReceiptCanonicalityMonitor(IBlockTree? blockTree, IReceiptStorage? receiptStorage, ILogManager? logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage?? throw new ArgumentNullException(nameof(receiptStorage));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree.BlockAddedToMain += OnBlockAddedToMain;
        }

        private void OnBlockAddedToMain(object sender, BlockReplacementEventArgs e)
        {
            // we don't want this to be on main processing thread
            Task.Run(() => RollbackReceipts(e.PreviousBlock))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error($"Couldn't correctly insert updated receipts from removed block {e.PreviousBlock?.ToString(Block.Format.FullHashAndNumber)} to receipt storage.", t.Exception);
                    }
                });
        }

        private void RollbackReceipts(Block? previousBlock)
        {
            if (previousBlock != null)
            {
                TxReceipt[] txReceipts = _receiptStorage.Get(previousBlock);
                foreach (var txReceipt in txReceipts)
                {
                    txReceipt.Removed = true;
                }
                _receiptStorage.Insert(previousBlock, txReceipts);
            }
        }

        public void Dispose()
        {
            _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
        }
    }
}
