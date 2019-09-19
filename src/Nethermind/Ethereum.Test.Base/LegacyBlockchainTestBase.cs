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
using System.Reflection.Metadata.Ecma335;
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
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.Forks;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.Store;
using Nethermind.Store.Repositories;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.Test.Base
{
    public abstract class LegacyBlockchainTestBase
    {
        private static ILogger _logger = new SimpleConsoleLogger();
        private static ILogManager _logManager = NullLogManager.Instance;
        private static ISealValidator Sealer { get; }
        private static DifficultyCalculatorWrapper DifficultyCalculator { get; }

        static LegacyBlockchainTestBase()
        {
            DifficultyCalculator = new DifficultyCalculatorWrapper();
            Sealer = new EthashSealValidator(_logManager, DifficultyCalculator, new Ethash(_logManager)); // temporarily keep reusing the same one as otherwise it would recreate cache for each test    
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

        protected async Task RunTest(LegacyBlockchainTest test, Stopwatch stopwatch = null)
        {
            TestContext.Write($"Running {test.Name} at {DateTime.UtcNow:HH:mm:ss.ffffff}");
            Assert.IsNull(test.LoadFailure, "test data loading failure");

            ISnapshotableDb stateDb = new StateDb();
            ISnapshotableDb codeDb = new StateDb();
            IDb traceDb = new MemDb();

            ISpecProvider specProvider;
            if (test.NetworkAfterTransition != null)
            {
                specProvider = new CustomSpecProvider(
                    (0, Frontier.Instance),
                    (1, test.Network),
                    (test.TransitionBlockNumber, test.NetworkAfterTransition));
            }
            else
            {
                specProvider = new CustomSpecProvider(
                    (0, Frontier.Instance), // TODO: this thing took a lot of time to find after it was removed!, genesis block is always initialized with Frontier
                    (1, test.Network));
            }

            if (specProvider.GenesisSpec != Frontier.Instance)
            {
                Assert.Fail("Expected genesis spec to be Frontier for blockchain tests");
            }

            DifficultyCalculator.Wrapped = new DifficultyCalculator(specProvider);
            IRewardCalculator rewardCalculator = new RewardCalculator(specProvider);

            IEthereumEcdsa ecdsa = new EthereumEcdsa(specProvider, _logManager);
            IStateProvider stateProvider = new StateProvider(stateDb, codeDb, _logManager);
            ITxPool transactionPool = new TxPool(NullTxStorage.Instance, new Timestamper(), ecdsa, specProvider, new TxPoolConfig(), stateProvider, _logManager);
            IReceiptStorage receiptStorage = NullReceiptStorage.Instance;
            var blockInfoDb = new MemDb();
            IBlockTree blockTree = new BlockTree(new MemDb(), new MemDb(), blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), specProvider, transactionPool, _logManager);
            IBlockhashProvider blockhashProvider = new BlockhashProvider(blockTree, _logManager);
            ITxValidator txValidator = new TxValidator(ChainId.MainNet);
            IHeaderValidator headerValidator = new HeaderValidator(blockTree, Sealer, specProvider, _logManager);
            IOmmersValidator ommersValidator = new OmmersValidator(blockTree, headerValidator, _logManager);
            IBlockValidator blockValidator = new BlockValidator(txValidator, headerValidator, ommersValidator, specProvider, _logManager);
            IStorageProvider storageProvider = new StorageProvider(stateDb, stateProvider, _logManager);
            IVirtualMachine virtualMachine = new VirtualMachine(
                stateProvider,
                storageProvider,
                blockhashProvider,
                specProvider,
                _logManager);

            IBlockProcessor blockProcessor = new BlockProcessor(
                specProvider,
                blockValidator,
                rewardCalculator,
                new TransactionProcessor(
                    specProvider,
                    stateProvider,
                    storageProvider,
                    virtualMachine,
                    _logManager),
                stateDb,
                codeDb,
                traceDb,
                stateProvider,
                storageProvider,
                transactionPool,
                receiptStorage,
                _logManager);

            IBlockchainProcessor blockchainProcessor = new BlockchainProcessor(
                blockTree,
                blockProcessor,
                new TxSignaturesRecoveryStep(ecdsa, NullTxPool.Instance, _logManager),
                _logManager,
                false,
                false);

            InitializeTestState(test, stateProvider, storageProvider, specProvider);

            List<(Block Block, string ExpectedException)> correctRlpsBlocks = new List<(Block, string)>();
            for (int i = 0; i < test.Blocks.Length; i++)
            {
                try
                {
                    TestBlockJson testBlockJson = test.Blocks[i];
                    var rlpContext = Bytes.FromHexString(testBlockJson.Rlp).AsRlpStream();
                    Block suggestedBlock = Rlp.Decode<Block>(rlpContext);
                    suggestedBlock.Header.SealEngineType = test.SealEngineUsed ? SealEngineType.Ethash : SealEngineType.None;

                    Assert.AreEqual(new Keccak(testBlockJson.BlockHeader.Hash), suggestedBlock.Header.Hash, "hash of the block");
                    for (int ommerIndex = 0; ommerIndex < suggestedBlock.Ommers.Length; ommerIndex++)
                    {
                        Assert.AreEqual(new Keccak(testBlockJson.UncleHeaders[ommerIndex].Hash), suggestedBlock.Ommers[ommerIndex].Hash, "hash of the ommer");
                    }

                    correctRlpsBlocks.Add((suggestedBlock, testBlockJson.ExpectedException));
                }
                catch (Exception)
                {
                    _logger?.Info($"Invalid RLP ({i})");
                }
            }

            if (correctRlpsBlocks.Count == 0)
            {
                Assert.AreEqual(new Keccak(test.GenesisBlockHeader.Hash), test.LastBlockHash);
                return;
            }

            if (test.GenesisRlp == null)
            {
                test.GenesisRlp = Rlp.Encode(new Block(JsonToBlockchainTest.Convert(test.GenesisBlockHeader)));
            }

            Block genesisBlock = Rlp.Decode<Block>(test.GenesisRlp.Bytes);
            Assert.AreEqual(new Keccak(test.GenesisBlockHeader.Hash), genesisBlock.Header.Hash, "genesis header hash");

            ManualResetEvent genesisProcessed = new ManualResetEvent(false);
            blockTree.NewHeadBlock += (sender, args) =>
            {
                if (args.Block.Number == 0)
                {
                    Assert.AreEqual(genesisBlock.Header.StateRoot, stateProvider.StateRoot, "genesis state root");
                    genesisProcessed.Set();
                }
            };

            blockchainProcessor.Start();
            blockTree.SuggestBlock(genesisBlock);

            genesisProcessed.WaitOne();

            for (int i = 0; i < correctRlpsBlocks.Count; i++)
            {
                stopwatch?.Start();
                try
                {
                    if (correctRlpsBlocks[i].ExpectedException != null)
                    {
                        _logger.Info($"Expecting block exception: {correctRlpsBlocks[i].ExpectedException}");
                    }

                    if (correctRlpsBlocks[i].Block.Hash == null)
                    {
                        throw new Exception($"null hash in {test.Name} block {i}");
                    }

                    // TODO: mimic the actual behaviour where block goes through validating sync manager?
                    if (!test.SealEngineUsed || blockValidator.ValidateSuggestedBlock(correctRlpsBlocks[i].Block))
                    {
                        blockTree.SuggestBlock(correctRlpsBlocks[i].Block);
                    }
                    else
                    {
                        Console.WriteLine("Invalid block");
                    }
                }
                catch (InvalidBlockException)
                {
                }
                catch (Exception ex)
                {
                    _logger?.Info(ex.ToString());
                }
            }

            await blockchainProcessor.StopAsync(true);
            stopwatch?.Stop();

            List<string> differences = RunAssertions(test, blockTree.RetrieveHeadBlock(), storageProvider, stateProvider);
//            if (differences.Any())
//            {
//                BlockTrace blockTrace = blockchainProcessor.TraceBlock(blockTree.BestSuggested.Hash);
//                _logger.Info(new UnforgivingJsonSerializer().Serialize(blockTrace, true));
//            }

            Assert.Zero(differences.Count, "differences");
        }

        private void InitializeTestState(LegacyBlockchainTest test, IStateProvider stateProvider, IStorageProvider storageProvider, ISpecProvider specProvider)
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

        private List<string> RunAssertions(LegacyBlockchainTest test, Block headBlock, IStorageProvider storageProvider, IStateProvider stateProvider)
        {
            if (test.PostStateRoot != null)
            {
                return test.PostStateRoot != stateProvider.StateRoot ? new List<string> {"state root mismatch"} : Enumerable.Empty<string>().ToList();
            }

            TestBlockHeaderJson testHeaderJson = test.Blocks
                                                     .Where(b => b.BlockHeader != null)
                                                     .SingleOrDefault(b => new Keccak(b.BlockHeader.Hash) == headBlock.Hash)?.BlockHeader ?? test.GenesisBlockHeader;
            BlockHeader testHeader = JsonToBlockchainTest.Convert(testHeaderJson);
            List<string> differences = new List<string>();

            var deletedAccounts = test.Pre.Where(pre => !test.PostState.ContainsKey(pre.Key));
            foreach (KeyValuePair<Address, AccountState> deletedAccount in deletedAccounts)
            {
                if (stateProvider.AccountExists(deletedAccount.Key))
                {
                    differences.Add($"Pre state account {deletedAccount.Key} was not deleted as expected.");
                }
            }

            foreach (KeyValuePair<Address, AccountState> accountState in test.PostState)
            {
                int differencesBefore = differences.Count;

                if (differences.Count > 8)
                {
                    Console.WriteLine("More than 8 differences...");
                    break;
                }

                bool accountExists = stateProvider.AccountExists(accountState.Key);
                BigInteger? balance = accountExists ? stateProvider.GetBalance(accountState.Key) : (BigInteger?) null;
                BigInteger? nonce = accountExists ? stateProvider.GetNonce(accountState.Key) : (BigInteger?) null;

                if (accountState.Value.Balance != balance)
                {
                    differences.Add($"{accountState.Key} balance exp: {accountState.Value.Balance}, actual: {balance}, diff: {balance - accountState.Value.Balance}");
                }

                if (accountState.Value.Nonce != nonce)
                {
                    differences.Add($"{accountState.Key} nonce exp: {accountState.Value.Nonce}, actual: {nonce}");
                }

                byte[] code = accountExists ? stateProvider.GetCode(accountState.Key) : new byte[0];
                if (!Bytes.AreEqual(accountState.Value.Code, code))
                {
                    differences.Add($"{accountState.Key} code exp: {accountState.Value.Code?.Length}, actual: {code?.Length}");
                }

                if (differences.Count != differencesBefore)
                {
                    _logger.Info($"ACCOUNT STATE ({accountState.Key}) HAS DIFFERENCES");
                }

                differencesBefore = differences.Count;

                KeyValuePair<UInt256, byte[]>[] clearedStorages = new KeyValuePair<UInt256, byte[]>[0];
                if (test.Pre.ContainsKey(accountState.Key))
                {
                    clearedStorages = test.Pre[accountState.Key].Storage.Where(s => !accountState.Value.Storage.ContainsKey(s.Key)).ToArray();
                }

                foreach (KeyValuePair<UInt256, byte[]> clearedStorage in clearedStorages)
                {
                    byte[] value = !stateProvider.AccountExists(accountState.Key) ? Bytes.Empty : storageProvider.Get(new StorageAddress(accountState.Key, clearedStorage.Key));
                    if (!value.IsZero())
                    {
                        differences.Add($"{accountState.Key} storage[{clearedStorage.Key}] exp: 0x00, actual: {value.ToHexString(true)}");
                    }
                }

                foreach (KeyValuePair<UInt256, byte[]> storageItem in accountState.Value.Storage)
                {
                    byte[] value = !stateProvider.AccountExists(accountState.Key) ? Bytes.Empty : storageProvider.Get(new StorageAddress(accountState.Key, storageItem.Key)) ?? new byte[0];
                    if (!Bytes.AreEqual(storageItem.Value, value))
                    {
                        differences.Add($"{accountState.Key} storage[{storageItem.Key}] exp: {storageItem.Value.ToHexString(true)}, actual: {value.ToHexString(true)}");
                    }
                }

                if (differences.Count != differencesBefore)
                {
                    _logger.Info($"ACCOUNT STORAGE ({accountState.Key}) HAS DIFFERENCES");
                }
            }

            BigInteger gasUsed = headBlock.Header.GasUsed;
            if ((testHeader?.GasUsed ?? 0) != gasUsed)
            {
                differences.Add($"GAS USED exp: {testHeader?.GasUsed ?? 0}, actual: {gasUsed}");
            }

            if (headBlock.Transactions.Any() && testHeader.Bloom.ToString() != headBlock.Header.Bloom.ToString())
            {
                differences.Add($"BLOOM exp: {testHeader.Bloom}, actual: {headBlock.Header.Bloom}");
            }

            if (testHeader.StateRoot != stateProvider.StateRoot)
            {
                differences.Add($"STATE ROOT exp: {testHeader.StateRoot}, actual: {stateProvider.StateRoot}");
            }

            if (testHeader.TxRoot != headBlock.Header.TxRoot)
            {
                differences.Add($"TRANSACTIONS ROOT exp: {testHeader.TxRoot}, actual: {headBlock.Header.TxRoot}");
            }

            if (testHeader.ReceiptsRoot != headBlock.Header.ReceiptsRoot)
            {
                differences.Add($"RECEIPT ROOT exp: {testHeader.ReceiptsRoot}, actual: {headBlock.Header.ReceiptsRoot}");
            }

            if (test.LastBlockHash != headBlock.Hash)
            {
                differences.Add($"LAST BLOCK HASH exp: {test.LastBlockHash}, actual: {headBlock.Hash}");
            }

            foreach (string difference in differences)
            {
                _logger.Info(difference);
            }

            return differences;
        }
    }
}