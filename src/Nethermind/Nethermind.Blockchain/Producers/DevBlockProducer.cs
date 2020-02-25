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

        public DevBlockProducer(IPendingTxSelector pendingTxSelector,
            IBlockchainProcessor processor,
            IStateProvider stateProvider,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue,
            ITxPool txPool,
            ITimestamper timestamper,
            ILogManager logManager) 
            : base(pendingTxSelector, processor, NethDevSealEngine.Instance, blockTree, blockProcessingQueue, stateProvider, timestamper, logManager)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        }

        public override void Start()
        {
            _txPool.NewPending += OnNewPendingTx;
        }

        public override async Task StopAsync()
        {
            _txPool.NewPending -= OnNewPendingTx;
            await Task.CompletedTask;
        }

        protected override UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp) => 1;

        private void OnNewPendingTx(object sender, TxEventArgs e)
        {
            TryProduceNewBlock(CancellationToken.None).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if(Logger.IsError) Logger.Error($"Failed to produce block after receiving transaction {e.Transaction}", t.Exception);
                }
            });
        }
    }
}