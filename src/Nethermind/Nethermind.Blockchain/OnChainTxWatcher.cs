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
using Nethermind.Core;
using Nethermind.Core.Specs;
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
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                _txPool.RemoveTransaction(block.Transactions[i].Hash, block.Number, true);
            }
            
            // the hash will only be the same during perf test runs / modified DB states
            if (previousBlock is not null)
            {
                bool isEip155Enabled = _specProvider.GetSpec(previousBlock.Number).IsEip155Enabled;
                for (int i = 0; i < previousBlock.Transactions.Length; i++)
                {
                    Transaction tx = previousBlock.Transactions[i];
                    _txPool.AddTransaction(tx, isEip155Enabled ? TxHandlingOptions.None : TxHandlingOptions.PreEip155Signing);
                }
            }
        }

        public void Dispose()
        {
            _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
        }
    }
}
