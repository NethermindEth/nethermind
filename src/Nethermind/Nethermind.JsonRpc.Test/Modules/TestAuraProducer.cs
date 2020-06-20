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

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.JsonRpc.Test.Modules
{
    public class TestAuraProducer : AuRaBlockProducer
    {
        public TestAuraProducer(ITxSource transactionSource,
            IBlockchainProcessor processor,
            IStateProvider stateProvider,
            ISealer sealer,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue,
            ITimestamper timestamper,
            ILogManager logManager,
            IAuRaStepCalculator auRaStepCalculator,
            IReportingValidator reportingValidator,
            IAuraConfig config) : base(transactionSource, processor, stateProvider, sealer, blockTree, blockProcessingQueue, timestamper, logManager, auRaStepCalculator, reportingValidator, config)
        {
        }

        private readonly AutoResetEvent _newBlockArrived = new AutoResetEvent(false);

        protected override async ValueTask ProducerLoop()
        {
            await _newBlockArrived.WaitOneAsync(CancellationToken.None);
            await TryProduceNewBlock(CancellationToken.None);
        }
    }
}
