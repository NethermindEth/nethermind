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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.AccountAbstraction.Contracts;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Blockchain.Filters;
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
using Nethermind.Db;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Network;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Newtonsoft.Json.Linq;
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
            public AbiDefinition EntryPointContractAbi { get; private set; } = null!;
            public UserOperationTxBuilder UserOperationTxBuilder { get; private set; } = null!;
            public UserOperationTxSource UserOperationTxSource { get; private set; } = null!;

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
                    EntryPointContractAddress = "0xdb8b5f6080a8e466b64a8d7458326cb650b3353f",
                    Create2FactoryAddress = "0xd75a3a95360e44a3874e691fb48d77855f127069",
                    UserOperationPoolSize = 200
                };
            public Address MinerAddress => TestItem.PrivateKeyD.Address;
            private IBlockValidator BlockValidator { get; set; } = null!;
            private ISigner Signer { get; }

            public override ILogManager LogManager => NUnitLogManager.Instance;
            
            protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
            {
                MiningConfig miningConfig = new() {MinGasPrice = UInt256.One};
                
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
                
                UserOperationTxSource = new(UserOperationTxBuilder, UserOperationPool, UserOperationSimulator, SpecProvider, LogManager.GetClassLogger());

                Eth2TestBlockProducerFactory producerFactory = new Eth2TestBlockProducerFactory(GasLimitCalculator, UserOperationTxSource);
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
                Address.TryParse(_accountAbstractionConfig.EntryPointContractAddress, out Address? entryPointContractAddress);
                Address.TryParse(_accountAbstractionConfig.Create2FactoryAddress, out Address? create2FactoryAddress);
                
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

                var parser = new AbiDefinitionParser();
                parser.RegisterAbiTypeFactory(new AbiTuple<UserOperationAbi>());
                var json = parser.LoadContract(typeof(EntryPoint));
                EntryPointContractAbi = parser.Parse(json);

                UserOperationTxBuilder = new UserOperationTxBuilder(
                    EntryPointContractAbi, 
                    Signer,
                    entryPointContractAddress!, 
                    SpecProvider, 
                    State);
                
                UserOperationSimulator = new(
                    UserOperationTxBuilder,
                    State,
                    StateReader,
                    EntryPointContractAbi,
                    create2FactoryAddress!,
                    entryPointContractAddress!,
                    SpecProvider, 
                    BlockTree, 
                    DbProvider, 
                    ReadOnlyTrieStore, 
                    Timestamper,
                    LogManager);
                
                UserOperationPool = new UserOperationPool(
                    _accountAbstractionConfig, 
                    BlockTree,
                    entryPointContractAddress!, 
                    LogManager.GetClassLogger(),
                    new PaymasterThrottler(), 
                    LogFinder, 
                    Signer, 
                    State, 
                    Timestamper, 
                    UserOperationSimulator, 
                    new UserOperationSortedPool(
                        _accountAbstractionConfig.UserOperationPoolSize, 
                        new CompareUserOperationsByDecreasingGasPrice(), 
                        LogManager),
                    SpecProvider.ChainId);
                
                return blockProcessor;
            }

            protected override async Task<TestBlockchain> Build(ISpecProvider? specProvider = null, UInt256? initialValues = null)
            {
                TestBlockchain chain = await base.Build(specProvider, initialValues);
                AccountAbstractionRpcModule = new AccountAbstractionRpcModule(UserOperationPool, new []{new Address(_accountAbstractionConfig.EntryPointContractAddress)});
                
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
                ResultWrapper<Keccak> resultOfUserOperation = UserOperationPool.AddUserOperation(userOperation);
                resultOfUserOperation.GetResult().ResultType.Should().NotBe(ResultType.Failure, resultOfUserOperation.Result.Error);
                resultOfUserOperation.GetData().Should().Be(userOperation.CalculateRequestId(new Address(_accountAbstractionConfig.EntryPointContractAddress), SpecProvider.ChainId));
            }
        }
    }
}
