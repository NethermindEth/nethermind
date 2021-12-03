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
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Mev;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin
    {
        private IMiningConfig _miningConfig = null!;
        private IMevConfig _mevConfig = null!;
        private IBlockProducer _blockProducer = null!;
        private IBlockProducer _emptyBlockProducer = null!;
        private IBlockBuilder _idealBlockBuilder = null!;
        private readonly Eth2BlockProductionContext _emptyBlockProductionContext = new();
        private readonly Eth2BlockProductionContext _idealBlockProductionContext = new();
        private ManualTimestamper? _manualTimestamper;
        private MevPlugin _mevPlugin = null!;

        public async Task<IBlockProducer> InitBlockProducer(IConsensusBlockProducer consensusPlugin)
        {
            if (_mergeConfig.Enabled)
            {
                _miningConfig = _api.Config<IMiningConfig>();
                _mevConfig = _api.Config<IMevConfig>();
                if (_api.EngineSigner == null) throw new ArgumentNullException(nameof(_api.EngineSigner));
                if (_api.ChainSpec == null) throw new ArgumentNullException(nameof(_api.ChainSpec));
                if (_api.BlockTree == null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.BlockProcessingQueue == null) throw new ArgumentNullException(nameof(_api.BlockProcessingQueue));
                if (_api.SpecProvider == null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_api.BlockValidator == null) throw new ArgumentNullException(nameof(_api.BlockValidator));
                if (_api.RewardCalculatorSource == null) throw new ArgumentNullException(nameof(_api.RewardCalculatorSource));
                if (_api.ReceiptStorage == null) throw new ArgumentNullException(nameof(_api.ReceiptStorage));
                if (_api.TxPool == null) throw new ArgumentNullException(nameof(_api.TxPool));
                if (_api.DbProvider == null) throw new ArgumentNullException(nameof(_api.DbProvider));
                if (_api.ReadOnlyTrieStore == null) throw new ArgumentNullException(nameof(_api.ReadOnlyTrieStore));
                if (_api.BlockchainProcessor == null) throw new ArgumentNullException(nameof(_api.BlockchainProcessor));
                if (_api.HeaderValidator == null) throw new ArgumentNullException(nameof(_api.HeaderValidator));
                
                _api.HeaderValidator = new PostMergeHeaderValidator(_poSSwitcher, _api.BlockTree, _api.SpecProvider, Always.Valid, _api.LogManager);
                _api.BlockValidator = new BlockValidator(_api.TxValidator, _api.HeaderValidator, Always.Valid,
                    _api.SpecProvider, _api.LogManager);

                ILogger logger = _api.LogManager.GetClassLogger();
                if (logger.IsWarn) logger.Warn("Starting ETH2 block producer & sealer");

                _manualTimestamper ??= new ManualTimestamper();
                _idealBlockProductionContext.Init(_api.BlockProducerEnvFactory);
                _emptyBlockProductionContext.Init(_api.BlockProducerEnvFactory);
                
                Eth2BlockProducerFactory blockProducerFactory = new(_api.SpecProvider, _api.SealEngine, _manualTimestamper, _miningConfig, _api.LogManager);

                IBlockProducer idealBlockProducer;
                IBlockProducer blockProducer;
                if (_mevConfig.Enabled)
                {
                    _mevPlugin = _api.GetConsensusWrapperPlugins()
                        .OfType<MevPlugin>()
                        .Single();
                    Eth2BlockProducerWrapper idealBlockProducerWrapper =
                        new (blockProducerFactory, _idealBlockProductionContext);
                    MevBlockProducer mevBlockProducer = await _mevPlugin.CreateMevBlockProducer(idealBlockProducerWrapper);
                    _idealBlockBuilder = mevBlockProducer;
                    idealBlockProducer = mevBlockProducer;
                    blockProducer = await _mevPlugin.InitBlockProducer(consensusPlugin);
                }
                else
                {
                    _idealBlockBuilder = new Eth2BlockBuilder(_idealBlockProductionContext);
                    idealBlockProducer = blockProducerFactory.Create(_idealBlockProductionContext);
                    blockProducer = await consensusPlugin.InitBlockProducer();
                }

                _idealBlockProductionContext.BlockProducer = idealBlockProducer;
                _api.BlockProducer = _blockProducer
                    = new MergeBlockProducer(blockProducer, idealBlockProducer, _poSSwitcher);
                
                _emptyBlockProducer = blockProducerFactory.Create(_emptyBlockProductionContext, EmptyTxSource.Instance);
                
                await _emptyBlockProducer.Start();
            }

            return _blockProducer;
        }

        public bool Enabled => _mergeConfig.Enabled;
    }
}
