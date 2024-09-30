// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
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
using Nethermind.TxPool;
using Nethermind.Verkle.Tree.TreeStore;
using NUnit.Framework;

namespace Ethereum.Test.Base
{
    public abstract class VerkleBlockChainTestBase
    {
        private static InterfaceLogger _logger = new NUnitLogger(LogLevel.Trace);
        // private static ILogManager _logManager = new OneLoggerLogManager(_logger);
        private static ILogManager _logManager = LimboLogs.Instance;
        private static ISealValidator Sealer { get; }
        private static DifficultyCalculatorWrapper DifficultyCalculator { get; }

        static VerkleBlockChainTestBase()
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
            Assert.IsNull(test.LoadFailure, "test data loading failure");

            IDbProvider dbProvider = TestMemDbProvider.Init();
            IDb codeDb = dbProvider.CodeDb;

            ISpecProvider specProvider = new CustomSpecProvider(
                    ((ForkActivation)0, Frontier.Instance),
                    ((ForkActivation)1, test.Network));

            if (specProvider.GenesisSpec != Frontier.Instance)
            {
                Assert.Fail("Expected genesis spec to be Frontier for blockchain tests");
            }

            if (test.Network is Cancun)
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

            IVerkleTreeStore verkleTreeStore = new VerkleTreeStore<VerkleSyncCache>(dbProvider, _logManager);
            VerkleStateTree verkleStateTree = new VerkleStateTree(verkleTreeStore, _logManager);

            IWorldState stateProvider = new VerkleWorldState(verkleTreeStore, codeDb, _logManager);
            IStateReader stateReader = new VerkleStateReader(verkleStateTree, codeDb, _logManager);

            IBlockTree blockTree = Build.A.BlockTree()
                .WithSpecProvider(specProvider)
                .WithoutSettingHead
                .TestObject;

            IReceiptStorage receiptStorage = NullReceiptStorage.Instance;
            IBlockhashProvider blockhashProvider = new BlockhashProvider(blockTree, _logManager);
            ITxValidator txValidator = new TxValidator(TestBlockchainIds.ChainId);
            IHeaderValidator headerValidator = new HeaderValidator(blockTree, Sealer, specProvider, _logManager);
            IUnclesValidator unclesValidator = new UnclesValidator(blockTree, headerValidator, _logManager);
            IBlockValidator blockValidator = new BlockValidator(txValidator, headerValidator, unclesValidator, specProvider, _logManager);
            IVirtualMachine virtualMachine = new VirtualMachine(
                blockhashProvider,
                specProvider,
                _logManager);

            BlockProcessor blockProcessor = new BlockProcessor(
                specProvider,
                blockValidator,
                rewardCalculator,
                new BlockProcessor.BlockValidationTransactionsExecutor(
                    new TransactionProcessor(
                        specProvider,
                        stateProvider,
                        virtualMachine,
                        _logManager),
                    stateProvider),
                stateProvider,
                receiptStorage,
                NullWitnessCollector.Instance,
                blockTree,
                _logManager);
            blockProcessor.ShouldVerifyIncomingWitness = true;

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

            return new EthereumTestResult
            (
                test.Name,
                null,
                true
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

                        if (testBlockJson.Witness is not null)
                        {
                            suggestedBlock.Body.ExecutionWitness = testBlockJson.Witness;
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
    }
}
