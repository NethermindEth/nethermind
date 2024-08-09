// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using System.Threading.Tasks;
using Ethereum.Test.Base.T8NUtils;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Consensus.BeaconBlockRoot;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Evm.Tracing.GethStyle;

namespace Ethereum.Test.Base
{
    public abstract class GeneralStateTestBase
    {
        private static ILogger _logger = new(new ConsoleAsyncLogger(LogLevel.Info));
        private static ILogManager _logManager = LimboLogs.Instance;
        private static readonly UInt256 _defaultBaseFeeForStateTest = 0xA;
        private readonly TxValidator _txValidator = new(MainnetSpecProvider.Instance.ChainId);
        private readonly BeaconBlockRootHandler _beaconBlockRootHandler = new();

        [SetUp]
        public void Setup()
        {
        }

        [OneTimeSetUp]
        public Task OneTimeSetUp() => KzgPolynomialCommitments.InitializeAsync();

        protected static void Setup(ILogManager logManager)
        {
            _logManager = logManager ?? LimboLogs.Instance;
            _logger = _logManager.GetClassLogger();
        }

        protected EthereumTestResult RunTest(GeneralStateTest test)
        {
            return RunTest(test, NullTxTracer.Instance);
        }

        protected EthereumTestResult RunTest(GeneralStateTest test, ITxTracer txTracer)
        {
            TestContext.Write($"Running {test.Name} at {DateTime.UtcNow:HH:mm:ss.ffffff}");
            Assert.IsNull(test.LoadFailure, "test data loading failure");

            IDb stateDb = new MemDb();
            IDb codeDb = new MemDb();

            ISpecProvider specProvider = new CustomSpecProvider(
                ((ForkActivation)0, Frontier.Instance), // TODO: this thing took a lot of time to find after it was removed!, genesis block is always initialized with Frontier
                ((ForkActivation)1, test.Fork));

            if (specProvider.GenesisSpec != Frontier.Instance)
            {
                Assert.Fail("Expected genesis spec to be Frontier for blockchain tests");
            }
            IReleaseSpec? spec = specProvider.GetSpec((ForkActivation)test.CurrentNumber);

            BlockHeader header = test.GetBlockHeader();
            BlockHeader? parentHeader = test.GetParentBlockHeader();

            if (test.Fork.IsEip1559Enabled)
            {
                test.CurrentBaseFee = header.BaseFeePerGas = CalculateBaseFeePerGas(test, parentHeader);
            }
            if (parentHeader != null)
            {
                header.ExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parentHeader, spec);
            }

            var blockhashProvider = GetBlockHashProvider(test, header, parentHeader);

            TrieStore trieStore = new(stateDb, _logManager);
            WorldState stateProvider = new(trieStore, codeDb, _logManager);
            CodeInfoRepository codeInfoRepository = new();
            IVirtualMachine virtualMachine = new VirtualMachine(
                blockhashProvider,
                specProvider,
                codeInfoRepository,
                _logManager);

            TransactionProcessor transactionProcessor = new(
                specProvider,
                stateProvider,
                virtualMachine,
                codeInfoRepository,
                _logManager);

            InitializeTestPreState(test.Pre, stateProvider, specProvider);

            var ecdsa = new EthereumEcdsa(specProvider.ChainId, _logManager);
            foreach (var transaction in test.Transactions)
            {
                transaction.ChainId ??= test.StateChainId;
                transaction.SenderAddress ??= ecdsa.RecoverAddress(transaction);
            }

            Stopwatch stopwatch = Stopwatch.StartNew();

            BlockHeader[] uncles = test.Ommers
                .Select(ommer => Build.A.BlockHeader
                    .WithNumber(test.CurrentNumber - ommer.Delta)
                    .WithBeneficiary(ommer.Address)
                    .TestObject)
                .ToArray();

            Block block = Build.A.Block.WithHeader(header).WithTransactions(test.Transactions)
                .WithWithdrawals(test.Withdrawals).WithUncles(uncles).TestObject;

            if (!test.IsStateTest)
            {
                var withdrawalProcessor = new WithdrawalProcessor(stateProvider, _logManager);
                withdrawalProcessor.ProcessWithdrawals(block, spec);
            }
            else if (test.Withdrawals.Length > 0)
            {
                throw new T8NException("withdrawals are not supported in state tests", ExitCodes.ErrorEVM);
            }


            CalculateReward(test.StateReward, test.IsStateTest, block, stateProvider, spec);
            BlockReceiptsTracer blockReceiptsTracer = new BlockReceiptsTracer();
            StorageTxTracer storageTxTracer = new();
            if (test.IsT8NTest)
            {
                CompositeBlockTracer compositeBlockTracer = new();
                compositeBlockTracer.Add(storageTxTracer);
                if (test.IsTraceEnabled)
                {
                    GethLikeBlockFileTracer gethLikeBlockFileTracer = new(block, test.GethTraceOptions, new FileSystem());
                    compositeBlockTracer.Add(gethLikeBlockFileTracer);
                }
                blockReceiptsTracer.SetOtherTracer(compositeBlockTracer);
            }
            blockReceiptsTracer.StartNewBlockTrace(block);

            if (!test.IsStateTest && test.ParentBeaconBlockRoot != null)
            {
                _beaconBlockRootHandler.ApplyContractStateChanges(block, spec, stateProvider, storageTxTracer);
            }

            int txIndex = 0;
            TransactionExecutionReport transactionExecutionReport = new();

            foreach (var tx in test.Transactions)
            {
                bool isValid = _txValidator.IsWellFormed(tx, spec, out string error) && IsValidBlock(block, specProvider);
                if (isValid)
                {
                    blockReceiptsTracer.StartNewTxTrace(tx);
                    TransactionResult transactionResult = transactionProcessor
                        .Execute(tx, new BlockExecutionContext(header), test.IsT8NTest ? blockReceiptsTracer : txTracer);
                    blockReceiptsTracer.EndTxTrace();

                    if (!test.IsT8NTest) continue;
                    transactionExecutionReport.ValidTransactions.Add(tx);
                    if (transactionResult.Success)
                    {
                        transactionExecutionReport.SuccessfulTransactions.Add(tx);
                        blockReceiptsTracer.LastReceipt.PostTransactionState = null;
                        blockReceiptsTracer.LastReceipt.BlockHash = null;
                        blockReceiptsTracer.LastReceipt.BlockNumber = 0;
                        transactionExecutionReport.SuccessfulTransactionReceipts.Add(blockReceiptsTracer.LastReceipt);
                    }
                    else if (transactionResult.Error != null)
                    {
                        transactionExecutionReport.RejectedTransactionReceipts.Add(new RejectedTx(txIndex, GethErrorMappings.GetErrorMapping(transactionResult.Error, tx.SenderAddress.ToString(true), tx.Nonce, stateProvider.GetNonce(tx.SenderAddress))));
                        stateProvider.Reset();
                    }
                    txIndex++;
                }
                else if (error != null)
                {
                    transactionExecutionReport.RejectedTransactionReceipts.Add(new RejectedTx(txIndex, GethErrorMappings.GetErrorMapping(error)));
                }
            }
            blockReceiptsTracer.EndBlockTrace();

            stopwatch.Stop();

            stateProvider.Commit(specProvider.GetSpec((ForkActivation)1));
            stateProvider.CommitTree(1);

            // '@winsvega added a 0-wei reward to the miner , so we had to add that into the state test execution phase. He needed it for retesteth.'
            if (!stateProvider.AccountExists(test.CurrentCoinbase))
            {
                stateProvider.CreateAccount(test.CurrentCoinbase, 0);
            }

            stateProvider.Commit(specProvider.GetSpec((ForkActivation)1));

            stateProvider.RecalculateStateRoot();

            List<string> differences = RunAssertions(test, stateProvider);
            EthereumTestResult testResult = new(test.Name, test.ForkName, differences.Count == 0);
            testResult.TimeInMs = stopwatch.Elapsed.TotalMilliseconds;
            testResult.StateRoot = stateProvider.StateRoot;

            if (test.IsT8NTest)
            {
                testResult.T8NResult = T8NResult.ConstructT8NResult(stateProvider, block, test, storageTxTracer, blockReceiptsTracer, specProvider, header, transactionExecutionReport);
            }

            return testResult;
        }

        private static IBlockhashProvider GetBlockHashProvider(GeneralStateTest test, BlockHeader header, BlockHeader? parent)
        {
            if (!test.IsT8NTest)
            {
                return new TestBlockhashProvider();
            }
            var t8NBlockHashProvider = new T8NBlockHashProvider();

            if (header.Hash != null) t8NBlockHashProvider.Insert(header.Hash, header.Number);
            if (parent?.Hash != null) t8NBlockHashProvider.Insert(parent.Hash, parent.Number);
            foreach (var blockHash in test.BlockHashes)
            {
                t8NBlockHashProvider.Insert(blockHash.Value, long.Parse(blockHash.Key));
            }
            return t8NBlockHashProvider;
        }

        private static UInt256 CalculateBaseFeePerGas(GeneralStateTest test, BlockHeader? parentHeader)
        {
            if (test.CurrentBaseFee.HasValue) return test.CurrentBaseFee.Value;
            return test.IsT8NTest ? BaseFeeCalculator.Calculate(parentHeader, test.Fork) : _defaultBaseFeeForStateTest;
        }

        private static void InitializeTestPreState(Dictionary<Address, AccountState> pre, WorldState stateProvider, ISpecProvider specProvider)
        {
            foreach (KeyValuePair<Address, AccountState> accountState in pre)
            {
                if (accountState.Value.Storage is not null)
                {
                    foreach (KeyValuePair<UInt256, byte[]> storageItem in accountState.Value.Storage)
                    {
                        stateProvider.Set(new StorageCell(accountState.Key, storageItem.Key),
                            storageItem.Value.WithoutLeadingZeros().ToArray());
                    }
                }

                stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance, accountState.Value.Nonce);
                if (accountState.Value.Code is not null)
                {
                    stateProvider.InsertCode(accountState.Key, accountState.Value.Code, specProvider.GenesisSpec);
                }
            }

            stateProvider.Commit(specProvider.GenesisSpec);
            stateProvider.CommitTree(0);
            stateProvider.Reset();
        }

        private bool IsValidBlock(Block block, ISpecProvider specProvider)
        {
            IBlockTree blockTree = Build.A.BlockTree()
                .WithSpecProvider(specProvider)
                .WithoutSettingHead
                .TestObject;

            var difficultyCalculator = new EthashDifficultyCalculator(specProvider);
            var sealer = new EthashSealValidator(_logManager, difficultyCalculator, new CryptoRandom(), new Ethash(_logManager), Timestamper.Default);
            IHeaderValidator headerValidator = new HeaderValidator(blockTree, sealer, specProvider, _logManager);
            IUnclesValidator unclesValidator = new UnclesValidator(blockTree, headerValidator, _logManager);
            IBlockValidator blockValidator = new BlockValidator(_txValidator, headerValidator, unclesValidator, specProvider, _logManager);

            return blockValidator.ValidateOrphanedBlock(block, out _);
        }

        private static void CalculateReward(string? stateReward, bool isStateTest, Block block, WorldState stateProvider, IReleaseSpec spec)
        {
            if (stateReward == null || isStateTest) return;

            var rewardCalculator = new RewardCalculator(UInt256.Parse(stateReward));
            BlockReward[] rewards = rewardCalculator.CalculateRewards(block);

            foreach (BlockReward reward in rewards)
            {
                if (!stateProvider.AccountExists(reward.Address))
                {
                    stateProvider.CreateAccount(reward.Address, reward.Value);
                }
                else
                {
                    stateProvider.AddToBalance(reward.Address, reward.Value, spec);
                }
            }
        }

        private List<string> RunAssertions(GeneralStateTest test, IWorldState stateProvider)
        {
            List<string> differences = [];
            if (test.IsT8NTest) return differences;
            if (test.PostHash != stateProvider.StateRoot)
            {
                differences.Add($"STATE ROOT exp: {test.PostHash}, actual: {stateProvider.StateRoot}");
            }

            foreach (string difference in differences)
            {
                _logger.Info(difference);
            }

            return differences;
        }
    }
}
