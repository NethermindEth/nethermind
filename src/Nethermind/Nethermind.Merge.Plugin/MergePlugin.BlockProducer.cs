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
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Runner.Ethereum.Steps;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin
    {
        private IMiningConfig _miningConfig = null!;
        private Eth2BlockProducer _blockProducer = null!;

        public Task<IBlockProducer> InitBlockProducer(ITxSource? txSource = null)
        {
            if (_mergeConfig.Enabled)
            {
                _miningConfig = _api.Config<IMiningConfig>();
                if (_api.EngineSigner == null) throw new StepDependencyException(nameof(_api.EngineSigner));
                if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
                if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
                if (_api.BlockProcessingQueue == null) throw new StepDependencyException(nameof(_api.BlockProcessingQueue));
                if (_api.StateProvider == null) throw new StepDependencyException(nameof(_api.StateProvider));
                if (_api.SpecProvider == null) throw new StepDependencyException(nameof(_api.SpecProvider));
                if (_api.BlockValidator == null) throw new StepDependencyException(nameof(_api.BlockValidator));
                if (_api.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
                if (_api.ReceiptStorage == null) throw new StepDependencyException(nameof(_api.ReceiptStorage));
                if (_api.TxPool == null) throw new StepDependencyException(nameof(_api.TxPool));
                if (_api.DbProvider == null) throw new StepDependencyException(nameof(_api.DbProvider));
                if (_api.ReadOnlyTrieStore == null) throw new StepDependencyException(nameof(_api.ReadOnlyTrieStore));

                ILogger logger = _api.LogManager.GetClassLogger();
                if (logger.IsWarn) logger.Warn("Starting ETH2 block producer & sealer");

                _api.BlockProducer = _blockProducer = new Eth2BlockProducerFactory(txSource).Create(
                    _api.BlockTree,
                    _api.DbProvider,
                    _api.ReadOnlyTrieStore,
                    _api.BlockPreprocessor,
                    _api.TxPool,
                    _api.BlockValidator,
                    _api.RewardCalculatorSource,
                    _api.ReceiptStorage,
                    _api.BlockProcessingQueue,
                    _api.StateProvider,
                    _api.SpecProvider,
                    _api.EngineSigner,
                    _miningConfig,
                    _api.LogManager
                );
            }

            return Task.FromResult((IBlockProducer)_blockProducer);
        }
    }
}
