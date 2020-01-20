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
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class DevBlockProducer : BaseBlockProducer
    {
        private readonly ITxPool _txPool;

        public DevBlockProducer(IPendingTransactionSelector pendingTransactionSelector,
            IBlockchainProcessor processor,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue,
            IStateProvider stateProvider,
            ITimestamper timestamper,
            ILogManager logManager, 
            ITxPool txPool) 
            : base(pendingTransactionSelector, processor, NullSealEngine.Instance, blockTree, blockProcessingQueue, stateProvider, timestamper, logManager)
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

        public async ValueTask ProduceEmptyBlock()
        {
            await base.TryProduceNewBlock(CancellationToken.None);
        }
        
        private async void OnNewPendingTx(object sender, TxEventArgs e)
        {
            await base.TryProduceNewBlock(CancellationToken.None);
        }
    }
}