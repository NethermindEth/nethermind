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

using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Mev
{
    public class MevBlockProducer : BlockProducerBase
    {
        public MevBlockProducer(
            ITxSource? txSource, 
            IBlockchainProcessor? processor, 
            ISealer? sealer, 
            IBlockTree? blockTree, 
            IBlockProcessingQueue? blockProcessingQueue,
            IStateProvider? stateProvider, 
            IGasLimitCalculator? gasLimitCalculator, 
            ITimestamper? timestamper, 
            ILogManager? logManager) 
            : base(txSource, processor, sealer, blockTree, blockProcessingQueue, stateProvider, gasLimitCalculator, timestamper, logManager)
        {
        }

        public override void Start()
        {
            throw new System.NotImplementedException();
        }

        public override Task StopAsync()
        {
            throw new System.NotImplementedException();
        }

        protected override bool IsRunning()
        {
            throw new System.NotImplementedException();
        }

        protected override UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp)
        {
            throw new System.NotImplementedException();
        }
    }
}
