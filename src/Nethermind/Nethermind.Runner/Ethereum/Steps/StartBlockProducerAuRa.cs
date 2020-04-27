﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.State;
using Nethermind.Store;
using Nethermind.Wallet;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeNetwork), typeof(InitializeFinalizationAuRa), typeof(SetupKeyStore))]
    public class StartBlockProducerAuRa : StartBlockProducer
    {
        private readonly AuRaEthereumRunnerContext _context;

        public StartBlockProducerAuRa(AuRaEthereumRunnerContext context) : base(context)
        {
            _context = context;
        }

        protected override void BuildProducer()
        {
            if (_context.NodeKey == null) throw new StepDependencyException(nameof(_context.NodeKey));
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            
            ILogger logger = _context.LogManager.GetClassLogger();
            if (logger.IsWarn) logger.Warn("Starting AuRa block producer & sealer");
            
            IAuRaStepCalculator stepCalculator = new AuRaStepCalculator(_context.ChainSpec.AuRa.StepDuration, _context.Timestamper);
            BlockProducerContext producerContext = GetProducerChain();
            var auraConfig = _context.Config<IAuraConfig>();
            _context.BlockProducer = new AuRaBlockProducer(
                producerContext.TxSource,
                producerContext.ChainProcessor,
                producerContext.ReadOnlyStateProvider,
                _context.Sealer,
                _context.BlockTree,
                _context.BlockProcessingQueue,
                _context.Timestamper,
                _context.LogManager,
                stepCalculator,
                auraConfig,
                _context.NodeKey.Address);
        }

        protected override BlockProcessor CreateBlockProcessor(ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv, IReadOnlyDbProvider readOnlyDbProvider)
        {
            if (_context.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_context.RewardCalculatorSource));
            if (_context.ValidatorStore == null) throw new StepDependencyException(nameof(_context.ValidatorStore));
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            
            var validator = new AuRaValidatorProcessorFactory(
                    readOnlyTxProcessingEnv.StateProvider,
                    _context.AbiEncoder,
                    readOnlyTxProcessingEnv.TransactionProcessor,
                    new ReadOnlyTransactionProcessorSource(readOnlyTxProcessingEnv),
                    readOnlyTxProcessingEnv.BlockTree,
                    _context.ReceiptStorage,
                    _context.ValidatorStore,
                    _context.LogManager)
                .CreateValidatorProcessor(_context.ChainSpec.AuRa.Validators);
            
            var blockProducer = new AuRaBlockProcessor(
                _context.SpecProvider,
                _context.BlockValidator,
                _context.RewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
                readOnlyTxProcessingEnv.TransactionProcessor,
                readOnlyDbProvider.StateDb,
                readOnlyDbProvider.CodeDb,
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyTxProcessingEnv.StorageProvider,
                _context.TxPool,
                _context.ReceiptStorage,
                _context.LogManager, 
                validator);
            
            validator.SetFinalizationManager(_context.FinalizationManager, true);

            return blockProducer;
        }

        protected override ITxSource CreateTxSourceForProducer(ReadOnlyTxProcessingEnv environment)
        {
            IList<RandomContract> GetRandomContracts(
                IDictionary<long, Address> randomnessContractAddress, 
                ITransactionProcessor transactionProcessor, 
                IAbiEncoder abiEncoder, 
                IStateProvider stateProvider,
                IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource, 
                Address nodeAddress) =>
                randomnessContractAddress
                    .Select(kvp => new RandomContract(transactionProcessor, 
                        abiEncoder, 
                        kvp.Value, 
                        stateProvider, 
                        readOnlyTransactionProcessorSource, 
                        kvp.Key, 
                        nodeAddress))
                    .ToArray();


            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));
            if (_context.NodeKey == null) throw new StepDependencyException(nameof(_context.NodeKey));

            var txSource = base.CreateTxSourceForProducer(environment);
            
            if (_context.ChainSpec.AuRa.RandomnessContractAddress?.Any() == true)
            {
                var randomContractTxSource = new RandomContractTxSource(
                    GetRandomContracts(_context.ChainSpec.AuRa.RandomnessContractAddress, 
                        environment.TransactionProcessor, _context.AbiEncoder, 
                        environment.StateProvider, 
                        new ReadOnlyTransactionProcessorSource(environment), 
                        _context.NodeKey.Address),
                    new EciesCipher(_context.CryptoRandom),
                    _context.NodeKey, 
                    _context.CryptoRandom);
                
                var systemTxSourceSigner = new GeneratedTxSourceApprover(randomContractTxSource, new BasicWallet(_context.NodeKey), _context.Timestamper, environment.StateReader, _context.BlockTree.ChainId);
                
                return new CompositeTxSource(systemTxSourceSigner, txSource);
            }
            else
            {
                return txSource;
            }
        }
    }
}