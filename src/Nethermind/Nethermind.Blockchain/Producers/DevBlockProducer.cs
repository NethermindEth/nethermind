//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Producers
{
    public class DevBlockProducer : BaseBlockProducer
    {
        private readonly ITxPool _txPool;
        private readonly SemaphoreSlim _newBlockLock = new SemaphoreSlim(1, 1);

        public DevBlockProducer(
            ITxSource txSource,
            IBlockchainProcessor processor,
            IStateProvider stateProvider,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue,
            ITxPool txPool,
            ITimestamper timestamper,
            ILogManager logManager) 
            : base(txSource, processor, NethDevSealEngine.Instance, blockTree, blockProcessingQueue, stateProvider, timestamper, logManager)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        }

        public override void Start()
        {
            _txPool.NewPending += OnNewPendingTx;
            BlockTree.NewHeadBlock += OnNewHeadBlock;
        }
        
        public override async Task StopAsync()
        {
            _txPool.NewPending -= OnNewPendingTx;
            BlockTree.NewHeadBlock -= OnNewHeadBlock;
            await Task.CompletedTask;
        }

        protected override UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp) => 1;

        private void OnNewPendingTx(object sender, TxEventArgs e)
        {
            OnNewPendingTxAsync(e);
        }

        private async void OnNewPendingTxAsync(TxEventArgs e)
        {
            _newBlockLock.Wait(TimeSpan.FromSeconds(1));
            try
            {
                if (!await TryProduceNewBlock(CancellationToken.None))
                {
                    _newBlockLock.Release();
                }
            }
            catch (Exception exception)
            {
                if (Logger.IsError) Logger.Error($"Failed to produce block after receiving transaction {e.Transaction}", exception);
                _newBlockLock.Release();
            }
        }

        private void OnNewHeadBlock(object sender, BlockEventArgs e)
        {
            if (_newBlockLock.CurrentCount == 0)
            {
                _newBlockLock.Release();
            }
        }
    }
    
}