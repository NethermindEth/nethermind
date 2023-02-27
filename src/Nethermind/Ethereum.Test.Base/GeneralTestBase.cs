// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
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

namespace Ethereum.Test.Base
{
    public abstract class GeneralStateTestBase
    {
        private static ILogger _logger = new ConsoleAsyncLogger(LogLevel.Info);
        private static ILogManager _logManager = LimboLogs.Instance;
        private static UInt256 _defaultBaseFeeForStateTest = 0xA;

        [SetUp]
        public void Setup()
        {
        }

        protected void Setup(ILogManager logManager)
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

            TrieStore trieStore = new(stateDb, _logManager);
            StateProvider stateProvider = new(trieStore, codeDb, _logManager);
            IBlockhashProvider blockhashProvider = new TestBlockhashProvider();
            IStorageProvider storageProvider = new StorageProvider(trieStore, stateProvider, _logManager);
            IVirtualMachine virtualMachine = new VirtualMachine(
                blockhashProvider,
                specProvider,
                _logManager);

            VirtualMachine._codeCache.Clear();

            TransactionProcessor transactionProcessor = new(
                specProvider,
                stateProvider,
                storageProvider,
                virtualMachine,
                _logManager);

            InitializeTestState(test, stateProvider, storageProvider, specProvider);

            BlockHeader header = new(test.PreviousHash, Keccak.OfAnEmptySequenceRlp, test.CurrentCoinbase,
                test.CurrentDifficulty, test.CurrentNumber, test.CurrentGasLimit, test.CurrentTimestamp, Array.Empty<byte>());
            header.BaseFeePerGas = test.Fork.IsEip1559Enabled ? test.CurrentBaseFee ?? _defaultBaseFeeForStateTest : UInt256.Zero;
            header.StateRoot = test.PostHash;
            header.Hash = header.CalculateHash();
            header.IsPostMerge = test.CurrentRandom is not null;
            header.MixHash = test.CurrentRandom;

            Stopwatch stopwatch = Stopwatch.StartNew();
            var txValidator = new TxValidator((MainnetSpecProvider.Instance.ChainId));
            var spec = specProvider.GetSpec((ForkActivation)test.CurrentNumber);
            if (test.Transaction.ChainId == null)
                test.Transaction.ChainId = MainnetSpecProvider.Instance.ChainId;
            bool isValid = txValidator.IsWellFormed(test.Transaction, spec);
            if (isValid)
                transactionProcessor.Execute(test.Transaction, header, txTracer);
            stopwatch.Stop();

            stateProvider.Commit(specProvider.GenesisSpec);
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
            testResult.TimeInMs = (int)stopwatch.Elapsed.TotalMilliseconds;
            testResult.StateRoot = stateProvider.StateRoot;

            //            Assert.Zero(differences.Count, "differences");
            return testResult;
        }

        private static void InitializeTestState(GeneralStateTest test, StateProvider stateProvider,
            IStorageProvider storageProvider, ISpecProvider specProvider)
        {
            foreach (KeyValuePair<Address, AccountState> accountState in test.Pre)
            {
                foreach (KeyValuePair<UInt256, byte[]> storageItem in accountState.Value.Storage)
                {
                    storageProvider.Set(new StorageCell(accountState.Key, storageItem.Key),
                        storageItem.Value.WithoutLeadingZeros().ToArray());
                }

                stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance);
                Keccak codeHash = stateProvider.UpdateCode(accountState.Value.Code);
                stateProvider.UpdateCodeHash(accountState.Key, codeHash, specProvider.GenesisSpec);
                stateProvider.SetNonce(accountState.Key, accountState.Value.Nonce);
            }

            storageProvider.Commit();
            stateProvider.Commit(specProvider.GenesisSpec);

            storageProvider.CommitTrees(0);
            stateProvider.CommitTree(0);

            storageProvider.Reset();
            stateProvider.Reset();
        }

        private List<string> RunAssertions(GeneralStateTest test, IStateProvider stateProvider)
        {
            List<string> differences = new();
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
