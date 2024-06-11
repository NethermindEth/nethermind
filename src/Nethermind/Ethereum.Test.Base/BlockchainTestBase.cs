// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Ethereum.Test.Base
{
    public abstract class BlockchainTestBase
    {
        private static InterfaceLogger _logger = new NUnitLogger(LogLevel.Trace);
        // private static ILogManager _logManager = new OneLoggerLogManager(_logger);
        private static ILogManager _logManager = LimboLogs.Instance;
        private static ISealValidator Sealer { get; }
        private static DifficultyCalculatorWrapper DifficultyCalculator { get; }

        static BlockchainTestBase()
        {
            DifficultyCalculator = new DifficultyCalculatorWrapper();
            Sealer = new EthashSealValidator(_logManager, DifficultyCalculator, new CryptoRandom(), new Ethash(_logManager), Timestamper.Default); // temporarily keep reusing the same one as otherwise it would recreate cache for each test
        }

        [SetUp]
        public void Setup()
        {
        }

        private class DifficultyCalculatorWrapper : IDifficultyCalculator
        {
            public IDifficultyCalculator? Wrapped { get; set; }

            public UInt256 Calculate(BlockHeader header, BlockHeader parent)
            {
                if (Wrapped is null)
                {
                    throw new InvalidOperationException(
                        $"Cannot calculate difficulty before the {nameof(Wrapped)} calculator is set.");
                }

                return Wrapped.Calculate(header, parent);
            }
        }

        protected async Task<EthereumTestResult> RunTest(BlockchainTest test, Stopwatch? stopwatch = null, bool failOnInvalidRlp = true)
        {
            TestContext.WriteLine($"Running {test.Name}, Network: [{test.Network.Name}] at {DateTime.UtcNow:HH:mm:ss.ffffff}");
            if (test.NetworkAfterTransition is not null)
                TestContext.WriteLine($"Network after transition: [{test.NetworkAfterTransition.Name}] at {test.TransitionForkActivation}");
            Assert.IsNull(test.LoadFailure, "test data loading failure");

            IDb stateDb = new MemDb();
            IDb codeDb = new MemDb();

            ISpecProvider specProvider;
            if (test.NetworkAfterTransition is not null)
            {
                specProvider = new CustomSpecProvider(
                    ((ForkActivation)0, Frontier.Instance),
                    ((ForkActivation)1, test.Network),
                    (test.TransitionForkActivation!.Value, test.NetworkAfterTransition));
            }
            else
            {
                specProvider = new CustomSpecProvider(
                    ((ForkActivation)0, Frontier.Instance), // TODO: this thing took a lot of time to find after it was removed!, genesis block is always initialized with Frontier
                    ((ForkActivation)1, test.Network));
            }

            if (specProvider.GenesisSpec != Frontier.Instance)
            {
                Assert.Fail("Expected genesis spec to be Frontier for blockchain tests");
            }

            if (test.Network is Cancun || test.NetworkAfterTransition is Cancun)
            {
                await KzgPolynomialCommitments.InitializeAsync();
            }

            DifficultyCalculator.Wrapped = new EthashDifficultyCalculator(specProvider);
            IRewardCalculator rewardCalculator = new RewardCalculator(specProvider);
            bool isPostMerge = test.Network != London.Instance &&
                               test.Network != Berlin.Instance &&
                               test.Network != MuirGlacier.Instance &&
                               test.Network != Istanbul.Instance &&
                               test.Network != ConstantinopleFix.Instance &&
                               test.Network != Constantinople.Instance &&
                               test.Network != Byzantium.Instance &&
                               test.Network != SpuriousDragon.Instance &&
                               test.Network != TangerineWhistle.Instance &&
                               test.Network != Dao.Instance &&
                               test.Network != Homestead.Instance &&
                               test.Network != Frontier.Instance &&
                               test.Network != Olympic.Instance;
            if (isPostMerge)
            {
                rewardCalculator = NoBlockRewards.Instance;
                specProvider.UpdateMergeTransitionInfo(0, 0);
            }

            IEthereumEcdsa ecdsa = new EthereumEcdsa(specProvider.ChainId, _logManager);

            TrieStore trieStore = new(stateDb, _logManager);
            IWorldState stateProvider = new WorldState(trieStore, codeDb, _logManager);
            IBlockTree blockTree = Build.A.BlockTree()
                .WithSpecProvider(specProvider)
                .WithoutSettingHead
                .TestObject;
            ITransactionComparerProvider transactionComparerProvider = new TransactionComparerProvider(specProvider, blockTree);
            IStateReader stateReader = new StateReader(trieStore, codeDb, _logManager);

            IReceiptStorage receiptStorage = NullReceiptStorage.Instance;
            IBlockhashProvider blockhashProvider = new BlockhashProvider(blockTree, specProvider, stateProvider, _logManager);
            ITxValidator txValidator = new TxValidator(TestBlockchainIds.ChainId);
            IHeaderValidator headerValidator = new HeaderValidator(blockTree, Sealer, specProvider, _logManager);
            IUnclesValidator unclesValidator = new UnclesValidator(blockTree, headerValidator, _logManager);
            IBlockValidator blockValidator = new BlockValidator(txValidator, headerValidator, unclesValidator, specProvider, _logManager);
            CodeInfoRepository codeInfoRepository = new();
            IVirtualMachine virtualMachine = new VirtualMachine(
                blockhashProvider,
                specProvider,
                codeInfoRepository,
                _logManager);

            IBlockProcessor blockProcessor = new BlockProcessor(
                specProvider,
                blockValidator,
                rewardCalculator,
                new BlockProcessor.BlockValidationTransactionsExecutor(
                    new TransactionProcessor(
                        specProvider,
                        stateProvider,
                        virtualMachine,
                        codeInfoRepository,
                        _logManager),
                    stateProvider),
                stateProvider,
                receiptStorage,
                new BlockhashStore(blockTree, specProvider, stateProvider),
                _logManager);

            IBlockchainProcessor blockchainProcessor = new BlockchainProcessor(
                blockTree,
                blockProcessor,
                new RecoverSignatures(ecdsa, NullTxPool.Instance, specProvider, _logManager),
                stateReader,
                _logManager,
                BlockchainProcessor.Options.NoReceipts);

            InitializeTestState(test, stateProvider, specProvider);

            stopwatch?.Start();
            List<(Block Block, string ExpectedException)> correctRlp = DecodeRlps(test, failOnInvalidRlp);

            test.GenesisRlp ??= Rlp.Encode(new Block(JsonToEthereumTest.Convert(test.GenesisBlockHeader)));

            Block genesisBlock = Rlp.Decode<Block>(test.GenesisRlp.Bytes);
            Assert.That(genesisBlock.Header.Hash, Is.EqualTo(new Hash256(test.GenesisBlockHeader.Hash)));

            ManualResetEvent genesisProcessed = new(false);

            blockTree.NewHeadBlock += (_, args) =>
            {
                if (args.Block.Number == 0)
                {
                    Assert.That(stateProvider.StateRoot, Is.EqualTo(genesisBlock.Header.StateRoot));
                    genesisProcessed.Set();
                }
            };

            blockchainProcessor.Start();
            blockTree.SuggestBlock(genesisBlock);

            genesisProcessed.WaitOne();
            for (int i = 0; i < correctRlp.Count; i++)
            {
                if (correctRlp[i].Block.Hash is null)
                {
                    Assert.Fail($"null hash in {test.Name} block {i}");
                }

                try
                {
                    // TODO: mimic the actual behaviour where block goes through validating sync manager?
                    correctRlp[i].Block.Header.IsPostMerge = correctRlp[i].Block.Difficulty == 0;
                    if (!test.SealEngineUsed || blockValidator.ValidateSuggestedBlock(correctRlp[i].Block, out _))
                    {
                        blockTree.SuggestBlock(correctRlp[i].Block);
                    }
                    else
                    {
                        if (correctRlp[i].ExpectedException is not null)
                        {
                            Assert.Fail($"Unexpected invalid block {correctRlp[i].Block.Hash}");
                        }
                    }
                }
                catch (InvalidBlockException e)
                {
                    if (correctRlp[i].ExpectedException is not null)
                    {
                        Assert.Fail($"Unexpected invalid block {correctRlp[i].Block.Hash}: {e}");
                    }
                }
                catch (Exception e)
                {
                    Assert.Fail($"Unexpected exception during processing: {e}");
                }
            }

            await blockchainProcessor.StopAsync(true);
            stopwatch?.Stop();

            List<string> differences = RunAssertions(test, blockTree.RetrieveHeadBlock(), stateProvider);

            Assert.Zero(differences.Count, "differences");

            return new EthereumTestResult
            (
                test.Name,
                null,
                differences.Count == 0
            );
        }

        private List<(Block Block, string ExpectedException)> DecodeRlps(BlockchainTest test, bool failOnInvalidRlp)
        {
            List<(Block Block, string ExpectedException)> correctRlp = new();
            for (int i = 0; i < test.Blocks.Length; i++)
            {
                TestBlockJson testBlockJson = test.Blocks[i];
                try
                {
                    var rlpContext = Bytes.FromHexString(testBlockJson.Rlp).AsRlpStream();
                    Block suggestedBlock = Rlp.Decode<Block>(rlpContext);
                    suggestedBlock.Header.SealEngineType =
                        test.SealEngineUsed ? SealEngineType.Ethash : SealEngineType.None;

                    if (testBlockJson.BlockHeader is not null)
                    {
                        Assert.That(suggestedBlock.Header.Hash, Is.EqualTo(new Hash256(testBlockJson.BlockHeader.Hash)));

                        for (int uncleIndex = 0; uncleIndex < suggestedBlock.Uncles.Length; uncleIndex++)
                        {
                            Assert.That(suggestedBlock.Uncles[uncleIndex].Hash, Is.EqualTo(new Hash256(testBlockJson.UncleHeaders[uncleIndex].Hash)));
                        }

                        correctRlp.Add((suggestedBlock, testBlockJson.ExpectedException));
                    }
                }
                catch (Exception e)
                {
                    if (testBlockJson.ExpectedException is null)
                    {
                        string invalidRlpMessage = $"Invalid RLP ({i}) {e}";
                        if (failOnInvalidRlp)
                        {
                            Assert.Fail(invalidRlpMessage);
                        }
                        else
                        {
                            // ForgedTests don't have ExpectedException and at the same time have invalid rlps
                            // Don't fail here. If test executed incorrectly will fail at last check
                            _logger.Warn(invalidRlpMessage);
                        }
                    }
                    else
                    {
                        _logger.Info($"Expected invalid RLP ({i})");
                    }
                }
            }

            if (correctRlp.Count == 0)
            {
                Assert.NotNull(test.GenesisBlockHeader);
                Assert.That(test.LastBlockHash, Is.EqualTo(new Hash256(test.GenesisBlockHeader.Hash)));
            }

            return correctRlp;
        }

        private void InitializeTestState(BlockchainTest test, IWorldState stateProvider, ISpecProvider specProvider)
        {
            foreach (KeyValuePair<Address, AccountState> accountState in
                ((IEnumerable<KeyValuePair<Address, AccountState>>)test.Pre ?? Array.Empty<KeyValuePair<Address, AccountState>>()))
            {
                foreach (KeyValuePair<UInt256, byte[]> storageItem in accountState.Value.Storage)
                {
                    stateProvider.Set(new StorageCell(accountState.Key, storageItem.Key), storageItem.Value);
                }

                stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance);
                stateProvider.InsertCode(accountState.Key, accountState.Value.Code, specProvider.GenesisSpec);
                for (int i = 0; i < accountState.Value.Nonce; i++)
                {
                    stateProvider.IncrementNonce(accountState.Key);
                }
            }

            stateProvider.Commit(specProvider.GenesisSpec);

            stateProvider.CommitTree(0);

            stateProvider.Reset();
        }

        private List<string> RunAssertions(BlockchainTest test, Block headBlock, IWorldState stateProvider)
        {
            if (test.PostStateRoot is not null)
            {
                return test.PostStateRoot != stateProvider.StateRoot ? new List<string> { "state root mismatch" } : Enumerable.Empty<string>().ToList();
            }

            TestBlockHeaderJson testHeaderJson = (test.Blocks?
                                                     .Where(b => b.BlockHeader is not null)
                                                     .SingleOrDefault(b => new Hash256(b.BlockHeader.Hash) == headBlock.Hash)?.BlockHeader) ?? test.GenesisBlockHeader;
            BlockHeader testHeader = JsonToEthereumTest.Convert(testHeaderJson);
            List<string> differences = new();

            IEnumerable<KeyValuePair<Address, AccountState>> deletedAccounts = test.Pre?
                .Where(pre => !(test.PostState?.ContainsKey(pre.Key) ?? false)) ?? Array.Empty<KeyValuePair<Address, AccountState>>();

            foreach (KeyValuePair<Address, AccountState> deletedAccount in deletedAccounts)
            {
                if (stateProvider.AccountExists(deletedAccount.Key))
                {
                    differences.Add($"Pre state account {deletedAccount.Key} was not deleted as expected.");
                }
            }

            foreach ((Address acountAddress, AccountState accountState) in test.PostState)
            {
                int differencesBefore = differences.Count;

                if (differences.Count > 8)
                {
                    Console.WriteLine("More than 8 differences...");
                    break;
                }

                bool accountExists = stateProvider.AccountExists(acountAddress);
                UInt256? balance = accountExists ? stateProvider.GetBalance(acountAddress) : (UInt256?)null;
                UInt256? nonce = accountExists ? stateProvider.GetNonce(acountAddress) : (UInt256?)null;

                if (accountState.Balance != balance)
                {
                    differences.Add($"{acountAddress} balance exp: {accountState.Balance}, actual: {balance}, diff: {(balance > accountState.Balance ? balance - accountState.Balance : accountState.Balance - balance)}");
                }

                if (accountState.Nonce != nonce)
                {
                    differences.Add($"{acountAddress} nonce exp: {accountState.Nonce}, actual: {nonce}");
                }

                byte[] code = accountExists ? stateProvider.GetCode(acountAddress) : new byte[0];
                if (!Bytes.AreEqual(accountState.Code, code))
                {
                    differences.Add($"{acountAddress} code exp: {accountState.Code?.Length}, actual: {code?.Length}");
                }

                if (differences.Count != differencesBefore)
                {
                    _logger.Info($"ACCOUNT STATE ({acountAddress}) HAS DIFFERENCES");
                }

                differencesBefore = differences.Count;

                KeyValuePair<UInt256, byte[]>[] clearedStorages = new KeyValuePair<UInt256, byte[]>[0];
                if (test.Pre.ContainsKey(acountAddress))
                {
                    clearedStorages = test.Pre[acountAddress].Storage.Where(s => !accountState.Storage.ContainsKey(s.Key)).ToArray();
                }

                foreach (KeyValuePair<UInt256, byte[]> clearedStorage in clearedStorages)
                {
                    ReadOnlySpan<byte> value = !stateProvider.AccountExists(acountAddress) ? Bytes.Empty : stateProvider.Get(new StorageCell(acountAddress, clearedStorage.Key));
                    if (!value.IsZero())
                    {
                        differences.Add($"{acountAddress} storage[{clearedStorage.Key}] exp: 0x00, actual: {value.ToHexString(true)}");
                    }
                }

                foreach (KeyValuePair<UInt256, byte[]> storageItem in accountState.Storage)
                {
                    ReadOnlySpan<byte> value = !stateProvider.AccountExists(acountAddress) ? Bytes.Empty : stateProvider.Get(new StorageCell(acountAddress, storageItem.Key));
                    if (!Bytes.AreEqual(storageItem.Value, value))
                    {
                        differences.Add($"{acountAddress} storage[{storageItem.Key}] exp: {storageItem.Value.ToHexString(true)}, actual: {value.ToHexString(true)}");
                    }
                }

                if (differences.Count != differencesBefore)
                {
                    _logger.Info($"ACCOUNT STORAGE ({acountAddress}) HAS DIFFERENCES");
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
