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

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.AccountAbstraction.Contracts;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Test;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;

namespace Nethermind.AccountAbstraction.Test
{
    public partial class AccountAbstractionRpcModuleTests
    {
        public static Task<TestAccountAbstractionRpcBlockchain> CreateChain(IReleaseSpec? releaseSpec = null,
            UInt256? initialBaseFeePerGas = null)
        {
            TestAccountAbstractionRpcBlockchain testMevRpcBlockchain = new(initialBaseFeePerGas);
            TestSpecProvider testSpecProvider = releaseSpec is not null
                ? new TestSpecProvider(releaseSpec)
                : new TestSpecProvider(London.Instance);
            testSpecProvider.ChainId = 1;
            return TestRpcBlockchain.ForTest(testMevRpcBlockchain).Build(testSpecProvider);
        }

        public class TestAccountAbstractionRpcBlockchain : TestRpcBlockchain
        {
            public IDictionary<Address, IUserOperationPool> UserOperationPool { get; private set; } =
                new Dictionary<Address, IUserOperationPool>();

            public IDictionary<Address, UserOperationSimulator> UserOperationSimulator { get; private set; } =
                new Dictionary<Address, UserOperationSimulator>();

            public AbiDefinition EntryPointContractAbi { get; private set; } = null!;

            public IDictionary<Address, UserOperationTxBuilder> UserOperationTxBuilder { get; private set; } =
                new Dictionary<Address, UserOperationTxBuilder>();

            public UserOperationTxSource UserOperationTxSource { get; private set; } = null!;
            public Address[] EntryPointAddresses { get; private set; } = null!;
            public Address[] WhitelistedPayamsters { get; private set; } = null!;
            
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
                    EntryPointContractAddresses = "0xb0894727fe4ff102e1f1c8a16f38afc7b859f215,0x96cc609c8f5458fb8a7da4d94b678e38ebf3d04e",
                    Create2FactoryAddress = "0xd75a3a95360e44a3874e691fb48d77855f127069",
                    UserOperationPoolSize = 200,
                    WhitelistedPaymasters = ""
                };
            public Address MinerAddress => TestItem.PrivateKeyD.Address;
            private ISigner Signer { get; }

            public override ILogManager LogManager => NUnitLogManager.Instance;

            protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer,
                ITransactionComparerProvider transactionComparerProvider)
            {
                MiningConfig miningConfig = new() { MinGasPrice = UInt256.One };
                SpecProvider.UpdateMergeTransitionInfo(1, 0);

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
                    LogManager)
                {
                    TransactionsExecutorFactory =
                        new AABlockProducerTransactionsExecutorFactory(
                            SpecProvider,
                            LogManager,
                            Signer,
                            EntryPointAddresses)
                };

                UserOperationTxSource = new(UserOperationTxBuilder, UserOperationPool, UserOperationSimulator, SpecProvider, State, Signer, LogManager.GetClassLogger());

                PostMergeBlockProducer CreatePostMergeBlockProducer(IBlockProductionTrigger blockProductionTrigger,
                    ITxSource? txSource = null)
                {
                    var blockProducerEnv = blockProducerEnvFactory.Create(txSource);
                    return new PostMergeBlockProducerFactory(SpecProvider, SealEngine, Timestamper, miningConfig,
                        LogManager, GasLimitCalculator).Create(
                        blockProducerEnv, blockProductionTrigger);
                }

                IBlockProducer blockProducer =
                    CreatePostMergeBlockProducer(BlockProductionTrigger, UserOperationTxSource);

                blockProducer.BlockProduced += OnBlockProduced;

                return blockProducer;
            }

            private void OnBlockProduced(object? sender, BlockEventArgs e)
            {
                BlockTree.SuggestBlock(e.Block, BlockTreeSuggestOptions.None, false);
                BlockchainProcessor.Process(e.Block!, GetProcessingOptions(), NullBlockTracer.Instance);
                BlockTree.UpdateMainChain(new[] { e.Block! }, true);
            }

            private ProcessingOptions GetProcessingOptions()
            {
                ProcessingOptions options = ProcessingOptions.None;
                options |= ProcessingOptions.StoreReceipts;
                return options;
            }


            protected override BlockProcessor CreateBlockProcessor()
            {
                // Address.TryParse(_accountAbstractionConfig.EntryPointContractAddress, out Address? entryPointContractAddress);
                IList<Address> entryPointContractAddresses = new List<Address>();
                IList<string> entryPointContractAddressesString = _accountAbstractionConfig.GetEntryPointAddresses().ToList();
                foreach (string addressString in entryPointContractAddressesString)
                {
                    bool parsed = Address.TryParse(
                        addressString,
                        out Address? entryPointContractAddress);
                    entryPointContractAddresses.Add(entryPointContractAddress!);
                }

                EntryPointAddresses = entryPointContractAddresses.ToArray();
                
                IList<Address> whitelistedPaymasters = new List<Address>();
                IList<string> whitelistedPaymastersString = _accountAbstractionConfig.GetWhitelistedPaymasters().ToList();
                foreach (string addressString in whitelistedPaymastersString){
                    bool parsed = Address.TryParse(
                        addressString,
                        out Address? whitelistedPaymaster);
                    whitelistedPaymasters.Add(whitelistedPaymaster!);
                }

                WhitelistedPayamsters = whitelistedPaymasters.ToArray();
                
                
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

                foreach (Address entryPoint in entryPointContractAddresses)
                {
                    UserOperationTxBuilder[entryPoint] = new UserOperationTxBuilder(
                        EntryPointContractAbi,
                        Signer,
                        entryPoint!,
                        SpecProvider,
                        State);
                }

                foreach (Address entryPoint in entryPointContractAddresses)
                {
                    UserOperationSimulator[entryPoint] = new(
                        UserOperationTxBuilder[entryPoint],
                        State,
                        StateReader,
                        EntryPointContractAbi,
                        create2FactoryAddress!,
                        entryPoint!,
                        WhitelistedPayamsters,
                        SpecProvider, 
                        BlockTree, 
                        DbProvider, 
                        ReadOnlyTrieStore, 
                        Timestamper,
                        LogManager);
                }

                IUserOperationBroadcaster broadcaster = new UserOperationBroadcaster(LogManager.GetClassLogger());

                foreach (Address entryPoint in entryPointContractAddresses)
                {
                    UserOperationPool[entryPoint] = new UserOperationPool(
                        _accountAbstractionConfig,
                        BlockTree,
                        entryPoint!,
                        LogManager.GetClassLogger(),
                        new PaymasterThrottler(),
                        LogFinder,
                        Signer,
                        State,
                        Timestamper,
                        UserOperationSimulator[entryPoint],
                        new UserOperationSortedPool(
                            _accountAbstractionConfig.UserOperationPoolSize,
                            new CompareUserOperationsByDecreasingGasPrice(),
                            LogManager,
                            _accountAbstractionConfig.MaximumUserOperationPerSender),
                        broadcaster,
                        SpecProvider.ChainId);
                }

                return blockProcessor;
            }

            protected override async Task<TestBlockchain> Build(ISpecProvider? specProvider = null,
                UInt256? initialValues = null)
            {
                TestBlockchain chain = await base.Build(specProvider, initialValues);
                IList<Address> entryPointContractAddresses = new List<Address>();
                IList<string> _entryPointContractAddressesString =
                    _accountAbstractionConfig.GetEntryPointAddresses().ToList();
                foreach (string _addressString in _entryPointContractAddressesString)
                {
                    bool parsed = Address.TryParse(
                        _addressString,
                        out Address? entryPointContractAddress);
                    entryPointContractAddresses.Add(entryPointContractAddress!);
                }

                AccountAbstractionRpcModule =
                    new AccountAbstractionRpcModule(UserOperationPool, entryPointContractAddresses.ToArray());

                return chain;
            }

            private IBlockValidator CreateBlockValidator()
            {
                HeaderValidator headerValidator = new(BlockTree, Always.Valid, SpecProvider, LogManager);

                return new BlockValidator(
                    new TxValidator(SpecProvider.ChainId),
                    headerValidator,
                    Always.Valid,
                    SpecProvider,
                    LogManager);
            }

            protected override Task AddBlocksOnStart() => Task.CompletedTask;

            public void SendUserOperation(Address entryPoint, UserOperation userOperation)
            {
                ResultWrapper<Keccak> resultOfUserOperation = UserOperationPool[entryPoint].AddUserOperation(userOperation);
                resultOfUserOperation.GetResult().ResultType.Should().NotBe(ResultType.Failure, resultOfUserOperation.Result.Error);
                resultOfUserOperation.GetData().Should().Be(userOperation.RequestId!);
            }

            public void SupportedEntryPoints()
            {
                ResultWrapper<Address[]> resultOfEntryPoints = AccountAbstractionRpcModule.eth_supportedEntryPoints();
                resultOfEntryPoints.GetResult().ResultType.Should()
                    .NotBe(ResultType.Failure, resultOfEntryPoints.Result.Error);
                IList<Address> entryPointContractAddresses = new List<Address>();
                IList<string> _entryPointContractAddressesString =
                    _accountAbstractionConfig.GetEntryPointAddresses().ToList();
                foreach (string _addressString in _entryPointContractAddressesString)
                {
                    bool parsed = Address.TryParse(
                        _addressString,
                        out Address? entryPointContractAddress);
                    entryPointContractAddresses.Add(entryPointContractAddress!);
                }

                Address[] eps = entryPointContractAddresses.ToArray();
                Address[] recieved_eps = (Address[])(resultOfEntryPoints.GetData());
                Assert.AreEqual(eps, recieved_eps);
            }
        }
    }
}
