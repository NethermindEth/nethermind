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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Consensus.Test;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.Access;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Mev;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;

namespace Nethermind.AccountAbstraction.Test
{
    public partial class AccountAbstractionRpcModuleTests
    {
        public static Task<TestAccountAbstractionRpcBlockchain> CreateChain(IReleaseSpec? releaseSpec = null, UInt256? initialBaseFeePerGas = null)
        {
            TestAccountAbstractionRpcBlockchain testMevRpcBlockchain = new(initialBaseFeePerGas);
            TestSpecProvider testSpecProvider = releaseSpec is not null ? new TestSpecProvider(releaseSpec) : new TestSpecProvider(London.Instance);
            testSpecProvider.ChainId = 1;
            return TestRpcBlockchain.ForTest(testMevRpcBlockchain).Build(testSpecProvider);
        }
        
        public class TestAccountAbstractionRpcBlockchain : TestRpcBlockchain
        {
            public UserOperationPool UserOperationPool { get; private set; } = null!;
            public UserOperationSimulator UserOperationSimulator { get; private set; } = null!;

            public TestAccountAbstractionRpcBlockchain(UInt256? initialBaseFeePerGas)
            {
                Signer = new Signer(1, TestItem.PrivateKeyD, LogManager);
                GenesisBlockBuilder = Core.Test.Builders.Build.A.Block.Genesis.Genesis
                    .WithTimestamp(UInt256.One)
                    .WithGasLimit(GasLimitCalculator.GasLimit)
                    .WithBaseFeePerGas(initialBaseFeePerGas ?? 0);
                
            }
            
            public IAccountAbstractionRpcModule AccountAbstractionRpcModule { get; set; } = Substitute.For<IAccountAbstractionRpcModule>();
            public ManualGasLimitCalculator GasLimitCalculator = new() {GasLimit = 10_000_000};
            private AccountAbstractionConfig _accountAbstractionConfig = new AccountAbstractionConfig() 
                {
                    Enabled = true, 
                    SingletonContractAddress = "0xd75a3a95360e44a3874e691fb48d77855f127069",
                    UserOperationPoolSize = 200
                };
            public Address MinerAddress => TestItem.PrivateKeyD.Address;
            private IBlockValidator BlockValidator { get; set; } = null!;
            private ISigner Signer { get; }

            public override ILogManager LogManager => NUnitLogManager.Instance;
            
            protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
            {
                MiningConfig miningConfig = new() {MinGasPrice = UInt256.One};

                UserOperationTxSource userOperationTxSource = new(UserOperationPool, UserOperationSimulator);

                BlockProducerEnvFactory blockProducerEnvFactory = new BlockProducerEnvFactory(
                    DbProvider,
                    BlockTree,
                    ReadOnlyTrieStore,
                    SpecProvider,
                    BlockValidator,
                    NoBlockRewards.Instance,
                    ReceiptStorage,
                    BlockPreprocessorStep,
                    TxPool,
                    transactionComparerProvider,
                    miningConfig,
                    LogManager);
                
                Eth2TestBlockProducerFactory producerFactory = new Eth2TestBlockProducerFactory(GasLimitCalculator, userOperationTxSource);
                Eth2BlockProducer blockProducer = producerFactory.Create(
                    blockProducerEnvFactory, 
                    BlockTree, 
                    BlockProductionTrigger, 
                    SpecProvider,
                    new Eth2Signer(MinerAddress), 
                    Timestamper, 
                    miningConfig, 
                    LogManager);

                return blockProducer;
            }

            protected override BlockProcessor CreateBlockProcessor()
            {
                Address.TryParse(_accountAbstractionConfig.SingletonContractAddress, out Address _singletonContractAddress);
                
                BlockValidator = CreateBlockValidator();
                BlockProcessor blockProcessor = new(
                    SpecProvider,
                    BlockValidator,
                    NoBlockRewards.Instance,
                    new BlockProcessor.BlockValidationTransactionsExecutor(TxProcessor, State),
                    State,
                    Storage,
                    ReceiptStorage,
                    NullWitnessCollector.Instance,
                    LogManager);

                UserOperationSimulator = new(
                    State, 
                    Signer, 
                    _accountAbstractionConfig, 
                    _singletonContractAddress!,
                    SpecProvider, 
                    BlockTree, 
                    DbProvider, 
                    ReadOnlyTrieStore, 
                    LogManager, 
                    BlockPreprocessorStep);
                
                IPeerManager peerManager = Substitute.For<IPeerManager>();
                
                UserOperationPool = new UserOperationPool(BlockTree, State, Timestamper, new AccessBlockTracer(
                    Array.Empty<Address>()), 
                    _accountAbstractionConfig, 
                    new Dictionary<Address, int>(),
                    new HashSet<Address>(),
                    peerManager,
                    new UserOperationSortedPool(_accountAbstractionConfig.UserOperationPoolSize, new CompareUserOperationsByDecreasingGasPrice(), LogManager),
                    UserOperationSimulator);
                
                return blockProcessor;
            }

            protected override async Task<TestBlockchain> Build(ISpecProvider specProvider = null, UInt256? initialValues = null)
            {
                TestBlockchain chain = await base.Build(specProvider, initialValues);
                AccountAbstractionRpcModule = new AccountAbstractionRpcModule(UserOperationPool);
                
                return chain;
            }
            
            private IBlockValidator CreateBlockValidator()
            {
                HeaderValidator headerValidator = new(BlockTree, new Eth2SealEngine(Signer), SpecProvider, LogManager);
                
                return new BlockValidator(
                    new TxValidator(SpecProvider.ChainId),
                    headerValidator,
                    Always.Valid,
                    SpecProvider,
                    LogManager);
            }

            protected override Task AddBlocksOnStart() => Task.CompletedTask;
            
            public void SendUserOperation(UserOperation userOperation)
            {
                bool result = UserOperationPool.AddUserOperation(userOperation);
                ResultWrapper<bool> resultOfUserOperation =  ResultWrapper<bool>.Success(result);
                resultOfUserOperation.GetResult().ResultType.Should().NotBe(ResultType.Failure);
                resultOfUserOperation.GetData().Should().Be(true);
            }
        }
    }
}
