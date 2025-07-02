// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autofac;
using NUnit.Framework;
using Nethermind.Config;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;

namespace Ethereum.Test.Base
{
    public abstract class GeneralStateTestBase
    {
        private static ILogger _logger;
        private static ILogManager _logManager = new TestLogManager(LogLevel.Info);
        private static readonly UInt256 _defaultBaseFeeForStateTest = 0xA;

        static GeneralStateTestBase()
        {
            _logManager ??= LimboLogs.Instance;
            _logger = _logManager.GetClassLogger();
            KzgPolynomialCommitments.InitializeAsync().Wait();
        }

        [SetUp]
        public void Setup()
        {
        }

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
            _logger.Info($"Running {test.Name} at {DateTime.UtcNow:HH:mm:ss.ffffff}");
            Assert.That(test.LoadFailure, Is.Null, "test data loading failure");

            EofValidator.Logger = _logger;

            test.Fork = ChainUtils.ResolveSpec(test.Fork, test.ChainId);

            ISpecProvider specProvider =
                new CustomSpecProvider(test.ChainId, test.ChainId,
                    ((ForkActivation)0, test.GenesisSpec), // TODO: this thing took a lot of time to find after it was removed!, genesis block is always initialized with Frontier
                    ((ForkActivation)1, test.Fork));

            if (test.ChainId != GnosisSpecProvider.Instance.ChainId && specProvider.GenesisSpec != Frontier.Instance)
            {
                Assert.Fail("Expected genesis spec to be Frontier for blockchain tests");
            }

            IConfigProvider configProvider = new ConfigProvider();
            using IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(configProvider))
                .AddSingleton<IBlockhashProvider>(new TestBlockhashProvider())
                .AddSingleton(specProvider)
                .AddSingleton(_logManager)
                .Build();

            MainBlockProcessingContext mainBlockProcessingContext = container.Resolve<MainBlockProcessingContext>();
            IWorldState stateProvider = mainBlockProcessingContext.WorldState;
            ITransactionProcessor transactionProcessor = mainBlockProcessingContext.TransactionProcessor;

            InitializeTestState(test.Pre, stateProvider, specProvider);

            BlockHeader header = new(
                test.PreviousHash,
                Keccak.OfAnEmptySequenceRlp,
                test.CurrentCoinbase,
                test.CurrentDifficulty,
                test.CurrentNumber,
                test.CurrentGasLimit,
                test.CurrentTimestamp,
                []);
            header.BaseFeePerGas = test.Fork.IsEip1559Enabled ? test.CurrentBaseFee ?? _defaultBaseFeeForStateTest : UInt256.Zero;
            header.StateRoot = test.PostHash;
            header.Hash = header.CalculateHash();
            header.IsPostMerge = test.CurrentRandom is not null;
            header.MixHash = test.CurrentRandom;
            header.WithdrawalsRoot = test.CurrentWithdrawalsRoot;
            header.ParentBeaconBlockRoot = test.CurrentBeaconRoot;
            header.ExcessBlobGas = test.CurrentExcessBlobGas ?? (test.Fork is Cancun ? 0ul : null);
            header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(test.Transaction);
            header.RequestsHash = test.RequestsHash;

            Stopwatch stopwatch = Stopwatch.StartNew();
            IReleaseSpec? spec = specProvider.GetSpec((ForkActivation)test.CurrentNumber);

            if (test.Transaction.ChainId is null)
                test.Transaction.ChainId = test.ChainId;
            if (test.ParentBlobGasUsed is not null && test.ParentExcessBlobGas is not null)
            {
                BlockHeader parent = new(
                    parentHash: Keccak.Zero,
                    unclesHash: Keccak.OfAnEmptySequenceRlp,
                    beneficiary: test.CurrentCoinbase,
                    difficulty: test.CurrentDifficulty,
                    number: test.CurrentNumber - 1,
                    gasLimit: test.CurrentGasLimit,
                    timestamp: test.CurrentTimestamp,
                    extraData: []
                )
                {
                    BlobGasUsed = (ulong)test.ParentBlobGasUsed,
                    ExcessBlobGas = (ulong)test.ParentExcessBlobGas,
                };
                header.ExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
            }

            ValidationResult txIsValid = new TxValidator(test.ChainId).IsWellFormed(test.Transaction, spec);
            TransactionResult? txResult = null;
            if (txIsValid)
            {
                txResult = transactionProcessor.Execute(test.Transaction, new BlockExecutionContext(header, spec), txTracer);
            }
            else
            {
                _logger.Info($"Skipping invalid tx with error: {txIsValid.Error}");
            }

            stopwatch.Stop();
            if (txResult is not null && txResult.Value == TransactionResult.Ok)
            {
                stateProvider.Commit(specProvider.GetSpec((ForkActivation)1));
                stateProvider.CommitTree(1);

                // '@winsvega added a 0-wei reward to the miner , so we had to add that into the state test execution phase. He needed it for retesteth.'
                if (!stateProvider.AccountExists(test.CurrentCoinbase))
                {
                    stateProvider.CreateAccount(test.CurrentCoinbase, 0);
                }

                stateProvider.Commit(specProvider.GetSpec((ForkActivation)1));

                stateProvider.RecalculateStateRoot();
            }
            else
            {
                stateProvider.Reset();
            }

            List<string> differences = RunAssertions(test, stateProvider);
            EthereumTestResult testResult = new(test.Name, test.ForkName, differences.Count == 0);
            testResult.TimeInMs = stopwatch.Elapsed.TotalMilliseconds;
            testResult.StateRoot = stateProvider.StateRoot;

            if (differences.Count > 0)
            {
                _logger.Info($"\nDifferences from expected\n{string.Join("\n", differences)}");
            }

            //            Assert.Zero(differences.Count, "differences");
            return testResult;
        }

        public static void InitializeTestState(Dictionary<Address, AccountState> preState, IWorldState stateProvider, ISpecProvider specProvider)
        {
            foreach (KeyValuePair<Address, AccountState> accountState in preState)
            {
                foreach (KeyValuePair<UInt256, byte[]> storageItem in accountState.Value.Storage)
                {
                    stateProvider.Set(new StorageCell(accountState.Key, storageItem.Key),
                        storageItem.Value.WithoutLeadingZeros().ToArray());
                }

                stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance);
                stateProvider.InsertCode(accountState.Key, accountState.Value.Code, specProvider.GenesisSpec);
                stateProvider.SetNonce(accountState.Key, accountState.Value.Nonce);
            }

            stateProvider.Commit(specProvider.GenesisSpec);
            stateProvider.CommitTree(0);
            stateProvider.Reset();
        }

        private List<string> RunAssertions(GeneralStateTest test, IWorldState stateProvider)
        {
            List<string> differences = [];
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
