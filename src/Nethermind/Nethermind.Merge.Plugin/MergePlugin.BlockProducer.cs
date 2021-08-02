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
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin
    {
        private IMiningConfig _miningConfig = null!;
        private Eth2BlockProducer _blockProducer = null!;
        private ManualTimestamper? _manualTimestamper;
        private readonly IManualBlockProductionTrigger _defaultBlockProductionTrigger = new BuildBlocksWhenRequested();

        public Task<IBlockProducer> InitBlockProducer(IBlockProductionTrigger? blockProductionTrigger = null, ITxSource? additionalTxSource = null)
        {
            if (_mergeConfig.Enabled)
            {
                _miningConfig = _api.Config<IMiningConfig>();
                if (_api.EngineSigner == null) throw new ArgumentNullException(nameof(_api.EngineSigner));
                if (_api.ChainSpec == null) throw new ArgumentNullException(nameof(_api.ChainSpec));
                if (_api.BlockTree == null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.BlockProcessingQueue == null) throw new ArgumentNullException(nameof(_api.BlockProcessingQueue));
                if (_api.StateProvider == null) throw new ArgumentNullException(nameof(_api.StateProvider));
                if (_api.SpecProvider == null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_api.BlockValidator == null) throw new ArgumentNullException(nameof(_api.BlockValidator));
                if (_api.RewardCalculatorSource == null) throw new ArgumentNullException(nameof(_api.RewardCalculatorSource));
                if (_api.ReceiptStorage == null) throw new ArgumentNullException(nameof(_api.ReceiptStorage));
                if (_api.TxPool == null) throw new ArgumentNullException(nameof(_api.TxPool));
                if (_api.DbProvider == null) throw new ArgumentNullException(nameof(_api.DbProvider));
                if (_api.ReadOnlyTrieStore == null) throw new ArgumentNullException(nameof(_api.ReadOnlyTrieStore));

                ILogger logger = _api.LogManager.GetClassLogger();
                if (logger.IsWarn) logger.Warn("Starting ETH2 block producer & sealer");

                _manualTimestamper ??= new ManualTimestamper();
                _api.BlockProducer = _blockProducer = new Eth2BlockProducerFactory(additionalTxSource).Create(
                    _api.BlockProducerEnvFactory,
                    _api.BlockTree,
                    blockProductionTrigger ?? DefaultBlockProductionTrigger,
                    _api.SpecProvider,
                    _api.EngineSigner,
                    _manualTimestamper,
                    _miningConfig,
                    _api.LogManager
                );
            }

            return Task.FromResult((IBlockProducer)_blockProducer);
        }

        public IBlockProductionTrigger DefaultBlockProductionTrigger => _defaultBlockProductionTrigger;
    }
}
