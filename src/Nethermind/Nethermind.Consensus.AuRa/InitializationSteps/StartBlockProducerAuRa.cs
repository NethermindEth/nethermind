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

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa.InitializationSteps
{
    public class StartBlockProducerAuRa
    {
        private readonly AuRaNethermindApi _api;
        
        private BlockProducerEnv? _blockProducerContext;
        private INethermindApi NethermindApi => _api;
        
        private readonly IAuraConfig _auraConfig;
        private IAuRaStepCalculator? _stepCalculator;

        public StartBlockProducerAuRa(AuRaNethermindApi api)
        {
            _api = api;
            _auraConfig = NethermindApi.Config<IAuraConfig>();
        }

        private IAuRaStepCalculator StepCalculator
        {
            get
            {
                return _stepCalculator ?? (_stepCalculator = new AuRaStepCalculator(_api.ChainSpec.AuRa.StepDuration, _api.Timestamper, _api.LogManager));
            }
        }

        public IBlockProductionTrigger CreateTrigger()
        {
            BuildBlocksOnAuRaSteps onAuRaSteps = new(StepCalculator, _api.LogManager);
            BuildBlocksOnlyWhenNotProcessing onlyWhenNotProcessing = new(
                onAuRaSteps, 
                _api.BlockProcessingQueue, 
                _api.BlockTree, 
                _api.LogManager, 
                !_auraConfig.AllowAuRaPrivateChains);
            
            _api.DisposeStack.Push((IAsyncDisposable) onlyWhenNotProcessing);

            return onlyWhenNotProcessing;
        }

        public Task<IBlockProducer> BuildProducer(IBlockProductionTrigger blockProductionTrigger, ITxSource? additionalTxSource = null)
        {
            if (_api.EngineSigner == null) throw new StepDependencyException(nameof(_api.EngineSigner));
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            
            ILogger logger = _api.LogManager.GetClassLogger();
            if (logger.IsWarn) logger.Warn("Starting AuRa block producer & sealer");

            _api.BlockProducerEnvFactory = new AuRaBlockProducerEnvFactory(_api, _auraConfig);
            BlockProducerEnv producerEnv = _api.BlockProducerEnvFactory.Create(additionalTxSource);

            _api.GasLimitCalculator = (producerEnv as AuRaBlockProducerEnv).GasLimitCalculator;
            
            IBlockProducer blockProducer = new AuRaBlockProducer(
                producerEnv.TxSource,
                producerEnv.ChainProcessor,
                blockProductionTrigger,
                producerEnv.ReadOnlyStateProvider,
                _api.Sealer,
                _api.BlockTree,
                _api.Timestamper,
                StepCalculator,
                _api.ReportingValidator,
                _auraConfig,
                _api.GasLimitCalculator,
                _api.SpecProvider,
                _api.LogManager);
            
            return Task.FromResult(blockProducer);
        }
    }
}
