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
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.TxPools.Storages;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.Forks;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.Store;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.Test.Base
{
    public abstract class BlockchainTestBase
    {
        private readonly IBlockchainTestsSource _testsSource;
        private static ILogger _logger = new SimpleConsoleLogger();
        private static ILogManager _logManager = NullLogManager.Instance;
        private static ISealValidator Sealer { get; }
        private static DifficultyCalculatorWrapper DifficultyCalculator { get; }

        static BlockchainTestBase()
        {
            DifficultyCalculator = new DifficultyCalculatorWrapper();
            Sealer = new EthashSealValidator(_logManager, DifficultyCalculator, new Ethash(_logManager)); // temporarily keep reusing the same one as otherwise it would recreate cache for each test    
        }

        protected BlockchainTestBase(IBlockchainTestsSource testsSource)
        {
            _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
        }

        [SetUp]
        public void Setup()
        {
        }

        private class LoggingTraceListener : System.Diagnostics.TraceListener
        {
            private readonly StringBuilder _line = new StringBuilder();

            public override void Write(string message)
            {
                _line.Append(message);
            }

            public override void WriteLine(string message)
            {
                Write(message);
                _logger.Info(_line.ToString());
                _line.Clear();
            }
        }

        protected void Setup(ILogManager logManager)
        {
            _logManager = logManager ?? NullLogManager.Instance;
            _logger = _logManager.GetClassLogger();
        }

        private class DifficultyCalculatorWrapper : IDifficultyCalculator
        {
            public IDifficultyCalculator Wrapped { get; set; }

            public UInt256 Calculate(UInt256 parentDifficulty, UInt256 parentTimestamp, UInt256 currentTimestamp, long blockNumber, bool parentHasUncles)
            {
                return Wrapped.Calculate(parentDifficulty, parentTimestamp, currentTimestamp, blockNumber, parentHasUncles);
            }
        }

        private class StateRootValidator : IBlockValidator
        {
            private readonly Keccak _stateRoot;

            public StateRootValidator(Keccak stateRoot)
            {
                _stateRoot = stateRoot ?? throw new ArgumentNullException(nameof(stateRoot));
            }

            public bool ValidateHash(BlockHeader header)
            {
                return true;
            }

            public bool ValidateHeader(BlockHeader header, BlockHeader parent, bool isOmmer)
            {
                return header.StateRoot == _stateRoot;
            }

            public bool ValidateHeader(BlockHeader header, bool isOmmer)
            {
                return header.StateRoot == _stateRoot;
            }

            public bool ValidateSuggestedBlock(Block block)
            {
                return true;
            }

            public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock)
            {
                return processedBlock.StateRoot == _stateRoot;
            }
        }

        public IEnumerable<BlockchainTest> LoadTests()
        {
            return _testsSource.LoadTests();
        }

        protected async Task RunTest(BlockchainTest test, Stopwatch stopwatch = null)
        {
            Assert.IsNull(test.LoadFailure, "test data loading failure");

            ISnapshotableDb stateDb = new StateDb();
            ISnapshotableDb codeDb = new StateDb();

            ISpecProvider specProvider = new CustomSpecProvider(
                (0, Frontier.Instance), // TODO: this thing took a lot of time to find after it was removed!, genesis block is always initialized with Frontier
                (1, test.Network));

            if (specProvider.GenesisSpec != Frontier.Instance)
            {
                Assert.Fail("Expected genesis spec to be Frontier for blockchain tests");
            }

            DifficultyCalculator.Wrapped = new DifficultyCalculator(specProvider);
            
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

            GethLikeTxTracer tracer = new GethLikeTxTracer(GethTraceOptions.Default);
            try
            {
                transactionProcessor.Execute(test.Transaction, header, NullTxTracer.Instance);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }

            // '@winsvega added a 0-wei reward to the miner , so we had to add that into the state test execution phase. He needed it for retesteth.'
            if (!stateProvider.AccountExists(test.CurrentCoinbase))
            {
                stateProvider.CreateAccount(test.CurrentCoinbase, 0);
            }

            List<string> differences = RunAssertions(test, stateProvider);
            if (differences.Count != 0)
            {
                string serialized = new EthereumJsonSerializer().Serialize(tracer.BuildResult());
                Console.WriteLine(serialized);
            }

            Assert.Zero(differences.Count, "differences");
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
                differences.Add($"LAST BLOCK HASH exp: {test.PostHash}, actual: {stateProvider.StateRoot}");
            }

            foreach (string difference in differences)
            {
                _logger.Info(difference);
            }

            return differences;
        }
    }
}