/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.Forks;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;
using NUnit.Framework;

namespace Ethereum.Test.Base
{
    public abstract class BlockchainTestBase
    {
        private static ILogger _logger = new SimpleConsoleLogger();
        private static ILogManager _logManager = NullLogManager.Instance;

        [SetUp]
        public void Setup()
        {
        }

        protected void Setup(ILogManager logManager)
        {
            _logManager = logManager ?? NullLogManager.Instance;
            _logger = _logManager.GetClassLogger();
        }
        
        protected EthereumTestResult RunTest(BlockchainTest test)
        {
            return RunTest(test, NullTxTracer.Instance);
        }
        
        protected EthereumTestResult RunTest(BlockchainTest test, ITxTracer txTracer)
        {
            TestContext.Write($"Running {test.Name} at {DateTime.UtcNow:HH:mm:ss.ffffff}");
            Stopwatch stopwatch = Stopwatch.StartNew();
            Assert.IsNull(test.LoadFailure, "test data loading failure");

            ISnapshotableDb stateDb = new StateDb();
            ISnapshotableDb codeDb = new StateDb();

            ISpecProvider specProvider = new CustomSpecProvider(1, 
                (0, Frontier.Instance), // TODO: this thing took a lot of time to find after it was removed!, genesis block is always initialized with Frontier
                (1, test.Fork));

            if (specProvider.GenesisSpec != Frontier.Instance)
            {
                Assert.Fail("Expected genesis spec to be Frontier for blockchain tests");
            }

            IStateProvider stateProvider = new StateProvider(stateDb, codeDb, _logManager);
            IBlockhashProvider blockhashProvider = new TestBlockhashProvider();
            IStorageProvider storageProvider = new StorageProvider(stateDb, stateProvider, _logManager);
            IVirtualMachine virtualMachine = new VirtualMachine(
                stateProvider,
                storageProvider,
                blockhashProvider,
                specProvider,
                _logManager);

            TransactionProcessor transactionProcessor = new TransactionProcessor(
                specProvider,
                stateProvider,
                storageProvider,
                virtualMachine,
                _logManager);

            InitializeTestState(test, stateProvider, storageProvider, specProvider);

            BlockHeader header = new BlockHeader(test.PreviousHash, Keccak.OfAnEmptySequenceRlp, test.CurrentCoinbase, test.CurrentDifficulty, test.CurrentNumber, test.CurrentGasLimit, test.CurrentTimestamp, new byte[0]);
            header.StateRoot = test.PostHash;
            header.Hash = Keccak.Compute("1");
            
            transactionProcessor.Execute(test.Transaction, header, txTracer);

            // '@winsvega added a 0-wei reward to the miner , so we had to add that into the state test execution phase. He needed it for retesteth.'
            if (!stateProvider.AccountExists(test.CurrentCoinbase))
            {
                stateProvider.CreateAccount(test.CurrentCoinbase, 0);
            }

            List<string> differences = RunAssertions(test, stateProvider);
            EthereumTestResult testResult = new EthereumTestResult();
            testResult.Pass = differences.Count == 0;
            testResult.Fork = test.ForkName;
            testResult.Name = test.Name;
            testResult.TimeInMs = (int)stopwatch.Elapsed.TotalMilliseconds;
            testResult.StateRoot = stateProvider.StateRoot;
            
//            Assert.Zero(differences.Count, "differences");
            return testResult;
        }

        private void InitializeTestState(BlockchainTest test, IStateProvider stateProvider, IStorageProvider storageProvider, ISpecProvider specProvider)
        {
            foreach (KeyValuePair<Address, AccountState> accountState in test.Pre)
            {
                foreach (KeyValuePair<UInt256, byte[]> storageItem in accountState.Value.Storage)
                {
                    storageProvider.Set(new StorageAddress(accountState.Key, storageItem.Key), storageItem.Value);
                }

                stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance);
                Keccak codeHash = stateProvider.UpdateCode(accountState.Value.Code);
                stateProvider.UpdateCodeHash(accountState.Key, codeHash, specProvider.GenesisSpec);
                for (int i = 0; i < accountState.Value.Nonce; i++)
                {
                    stateProvider.IncrementNonce(accountState.Key);
                }
            }

            storageProvider.Commit();
            stateProvider.Commit(specProvider.GenesisSpec);

            storageProvider.CommitTrees();
            stateProvider.CommitTree();

            storageProvider.Reset();
            stateProvider.Reset();
        }

        private List<string> RunAssertions(BlockchainTest test, IStateProvider stateProvider)
        {
            List<string> differences = new List<string>();
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