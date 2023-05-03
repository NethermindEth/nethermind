// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;
using Nethermind.Config;

namespace Nethermind.Clique.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class CliqueBlockProducerTests
    {
        private class On
        {
            private ILogManager _logManager = LimboLogs.Instance;
            //            private ILogManager _logManager = new OneLoggerLogManager(new ConsoleAsyncLogger(LogLevel.Debug));
            private ILogger _logger;
            private static ITimestamper _timestamper = Timestamper.Default;
            private CliqueConfig _cliqueConfig;
            private EthereumEcdsa _ethereumEcdsa = new(BlockchainIds.Goerli, LimboLogs.Instance);
            private Dictionary<PrivateKey, ILogManager> _logManagers = new();
            private Dictionary<PrivateKey, ISnapshotManager> _snapshotManager = new();
            private Dictionary<PrivateKey, BlockTree> _blockTrees = new();
            private Dictionary<PrivateKey, AutoResetEvent> _blockEvents = new();
            private Dictionary<PrivateKey, CliqueBlockProducer> _producers = new();
            private Dictionary<PrivateKey, TxPool.TxPool> _pools = new();

            private On()
                : this(15)
            {
            }

            private On(ulong blockPeriod)
            {
                _logger = _logManager.GetClassLogger();
                _cliqueConfig = new CliqueConfig();
                _cliqueConfig.BlockPeriod = blockPeriod;
                _cliqueConfig.Epoch = 30000;
                _genesis = GetGenesis();
                _genesis3Validators = GetGenesis(3);
            }

            public On CreateNode(PrivateKey privateKey, bool withGenesisAlreadyProcessed = false)
            {
                if (_logger.IsInfo) _logger.Info($"CREATING NODE {privateKey.Address}");
                _logManagers[privateKey] = LimboLogs.Instance;
                //                _logManagers[privateKey] = new OneLoggerLogManager(new ConsoleAsyncLogger(LogLevel.Debug, $"{privateKey.Address} "));
                ILogManager nodeLogManager = _logManagers[privateKey];

                AutoResetEvent newHeadBlockEvent = new(false);
                _blockEvents.Add(privateKey, newHeadBlockEvent);

                MemDb blocksDb = new();
                MemDb headersDb = new();
                MemDb blockInfoDb = new();

                MemDb stateDb = new();
                MemDb codeDb = new();

                ISpecProvider specProvider = RinkebySpecProvider.Instance;

                var trieStore = new TrieStore(stateDb, nodeLogManager);
                StateReader stateReader = new(trieStore, codeDb, nodeLogManager);
                StateProvider stateProvider = new(trieStore, codeDb, nodeLogManager);
                stateProvider.CreateAccount(TestItem.PrivateKeyD.Address, 100.Ether());
                GoerliSpecProvider goerliSpecProvider = GoerliSpecProvider.Instance;
                stateProvider.Commit(goerliSpecProvider.GenesisSpec);
                stateProvider.CommitTree(0);

                BlockTree blockTree = new(blocksDb, headersDb, blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), goerliSpecProvider, NullBloomStorage.Instance, nodeLogManager);

                blockTree.NewHeadBlock += (sender, args) => { _blockEvents[privateKey].Set(); };
                ITransactionComparerProvider transactionComparerProvider =
                    new TransactionComparerProvider(specProvider, blockTree);

                TxPool.TxPool txPool = new(_ethereumEcdsa, new ChainHeadInfoProvider(new FixedForkActivationChainHeadSpecProvider(GoerliSpecProvider.Instance), blockTree, stateProvider), new TxPoolConfig(), new TxValidator(goerliSpecProvider.ChainId), _logManager, transactionComparerProvider.GetDefaultComparer());
                _pools[privateKey] = txPool;

                BlockhashProvider blockhashProvider = new(blockTree, LimboLogs.Instance);
                _blockTrees.Add(privateKey, blockTree);

                SnapshotManager snapshotManager = new(_cliqueConfig, blocksDb, blockTree, _ethereumEcdsa, nodeLogManager);
                _snapshotManager[privateKey] = snapshotManager;
                CliqueSealer cliqueSealer = new(new Signer(BlockchainIds.Goerli, privateKey, LimboLogs.Instance), _cliqueConfig, snapshotManager, nodeLogManager);

                _genesis.Header.StateRoot = _genesis3Validators.Header.StateRoot = stateProvider.StateRoot;
                _genesis.Header.Hash = _genesis.Header.CalculateHash();
                _genesis3Validators.Header.Hash = _genesis3Validators.Header.CalculateHash();

                StorageProvider storageProvider = new(trieStore, stateProvider, nodeLogManager);
                TransactionProcessor transactionProcessor = new(goerliSpecProvider, stateProvider, storageProvider, new VirtualMachine(blockhashProvider, specProvider, nodeLogManager), nodeLogManager);
                BlockProcessor blockProcessor = new(
                    goerliSpecProvider,
                    Always.Valid,
                    NoBlockRewards.Instance,
                    new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider),
                    stateProvider,
                    storageProvider,
                    NullReceiptStorage.Instance,
                    NullWitnessCollector.Instance,
                    nodeLogManager);

                BlockchainProcessor processor = new(blockTree, blockProcessor, new AuthorRecoveryStep(snapshotManager), stateReader, nodeLogManager, BlockchainProcessor.Options.NoReceipts);
                processor.Start();

                IReadOnlyTrieStore minerTrieStore = trieStore.AsReadOnly();

                StateProvider minerStateProvider = new(minerTrieStore, codeDb, nodeLogManager);
                StorageProvider minerStorageProvider = new(minerTrieStore, minerStateProvider, nodeLogManager);
                VirtualMachine minerVirtualMachine = new(blockhashProvider, specProvider, nodeLogManager);
                TransactionProcessor minerTransactionProcessor = new(goerliSpecProvider, minerStateProvider, minerStorageProvider, minerVirtualMachine, nodeLogManager);

                BlockProcessor minerBlockProcessor = new(
                    goerliSpecProvider,
                    Always.Valid,
                    NoBlockRewards.Instance,
                    new BlockProcessor.BlockProductionTransactionsExecutor(minerTransactionProcessor, minerStateProvider, minerStorageProvider, goerliSpecProvider, _logManager),
                    minerStateProvider,
                    minerStorageProvider,
                    NullReceiptStorage.Instance,
                    NullWitnessCollector.Instance,
                    nodeLogManager);

                BlockchainProcessor minerProcessor = new(blockTree, minerBlockProcessor, new AuthorRecoveryStep(snapshotManager), stateReader, nodeLogManager, BlockchainProcessor.Options.NoReceipts);

                if (withGenesisAlreadyProcessed)
                {
                    ProcessGenesis(privateKey);
                }
                BlocksConfig blocksConfig = new()
                {
                    MinGasPrice = 0
                };
                ITxFilterPipeline txFilterPipeline = TxFilterPipelineBuilder.CreateStandardFilteringPipeline(nodeLogManager, specProvider, blocksConfig);
                TxPoolTxSource txPoolTxSource = new(txPool, specProvider, transactionComparerProvider, nodeLogManager, txFilterPipeline);
                CliqueBlockProducer blockProducer = new(
                    txPoolTxSource,
                    minerProcessor,
                    minerStateProvider,
                    blockTree,
                    _timestamper,
                    new CryptoRandom(),
                    snapshotManager,
                    cliqueSealer,
                    new TargetAdjustedGasLimitCalculator(goerliSpecProvider, new BlocksConfig()),
                    MainnetSpecProvider.Instance,
                    _cliqueConfig,
                    nodeLogManager);
                blockProducer.Start();

                ProducedBlockSuggester suggester = new ProducedBlockSuggester(blockTree, blockProducer);

                _producers.Add(privateKey, blockProducer);

                return this;
            }

            public static On Goerli => new();

            public static On FastGoerli => new(1);

            private Block _genesis3Validators;

            private Block _genesis;

            private Block GetGenesis(int validatorsCount = 2)
            {
                Keccak parentHash = Keccak.Zero;
                Keccak unclesHash = Keccak.OfAnEmptySequenceRlp;
                Address beneficiary = Address.Zero;
                UInt256 difficulty = new(1);
                long number = 0L;
                int gasLimit = 4700000;
                ulong timestamp = _timestamper.UnixTime.Seconds - _cliqueConfig.BlockPeriod;
                string extraDataHex = "0x2249276d20646f6e652077616974696e672e2e2e20666f7220626c6f636b2066";
                extraDataHex += TestItem.PrivateKeyA.Address.ToString(false).Replace("0x", string.Empty);
                extraDataHex += TestItem.PrivateKeyB.Address.ToString(false).Replace("0x", string.Empty);
                if (validatorsCount > 2)
                {
                    extraDataHex += TestItem.PrivateKeyC.Address.ToString(false).Replace("0x", string.Empty);
                }

                extraDataHex += "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

                byte[] extraData = Bytes.FromHexString(extraDataHex);
                BlockHeader header = new(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData);
                Block genesis = new(header);
                genesis.Header.Hash = genesis.Header.CalculateHash();
                genesis.Header.StateRoot = Keccak.EmptyTreeHash;
                genesis.Header.TxRoot = Keccak.EmptyTreeHash;
                genesis.Header.ReceiptsRoot = Keccak.EmptyTreeHash;
                genesis.Header.Bloom = Bloom.Empty;

                return genesis;
            }

            public On VoteToInclude(PrivateKey nodeId, Address address)
            {
                if (_logger.IsInfo) _logger.Info($"VOTE {address} IN");
                _producers[nodeId].CastVote(address, true);
                return this;
            }

            public On UncastVote(PrivateKey nodeId, Address address)
            {
                if (_logger.IsInfo) _logger.Info($"UNCAST VOTE ON {address}");
                _producers[nodeId].UncastVote(address);
                return this;
            }

            public On IsProducingBlocks(PrivateKey nodeId, bool expected, ulong? maxInterval)
            {
                if (_logger.IsInfo) _logger.Info($"IsProducingBlocks");
                Assert.That(((IBlockProducer)_producers[nodeId]).IsProducingBlocks(maxInterval), Is.EqualTo(expected));
                return this;
            }

            public On VoteToExclude(PrivateKey nodeId, Address address)
            {
                if (_logger.IsInfo) _logger.Info($"VOTE {address} OUT");
                _producers[nodeId].CastVote(address, false);
                return this;
            }

            public On ProcessGenesis()
            {
                foreach (KeyValuePair<PrivateKey, BlockTree> node in _blockTrees)
                {
                    ProcessGenesis(node.Key);
                }

                return this;
            }

            public On ProcessGenesis3Validators()
            {
                foreach (KeyValuePair<PrivateKey, BlockTree> node in _blockTrees)
                {
                    ProcessGenesis3Validators(node.Key);
                }

                return this;
            }

            public On ProcessBadGenesis()
            {
                foreach (KeyValuePair<PrivateKey, BlockTree> node in _blockTrees)
                {
                    ProcessBadGenesis(node.Key);
                }

                return this;
            }

            public On ProcessGenesis(PrivateKey nodeKey)
            {
                if (_logger.IsInfo) _logger.Info($"SUGGESTING GENESIS ON {nodeKey.Address}");
                _blockTrees[nodeKey].SuggestBlock(_genesis);
                _blockEvents[nodeKey].WaitOne(_timeout);
                return this;
            }

            public On ProcessGenesis3Validators(PrivateKey nodeKey)
            {
                _blockTrees[nodeKey].SuggestBlock(_genesis3Validators);
                _blockEvents[nodeKey].WaitOne(_timeout);
                return this;
            }

            public On ProcessBadGenesis(PrivateKey nodeKey)
            {
                Wait(10); // wait a moment so the timestamp changes
                if (_logger.IsInfo) _logger.Info($"SUGGESTING BAD GENESIS ON {nodeKey.Address}");
                _blockTrees[nodeKey].SuggestBlock(GetGenesis());
                _blockEvents[nodeKey].WaitOne(_timeout);
                return this;
            }

            public On Process(PrivateKey nodeKey, Block block)
            {
                if (_logger.IsInfo) _logger.Info($"SUGGESTING BLOCK {block.ToString(Block.Format.Short)} ON {nodeKey.Address}");
                try
                {
                    _blockTrees[nodeKey].SuggestBlock(block);
                    _blockEvents[nodeKey].WaitOne(_timeout);
                    return this;
                }
                catch (Exception e)
                {
                    _logger.Error("PROCESS ERROR", e);
                    throw;
                }
            }
            public On AssertHeadBlockParentIs(PrivateKey nodeKey, Keccak hash)
            {
                if (_logger.IsInfo) _logger.Info($"ASSERTING HEAD PARENT HASH ON {nodeKey.Address}");
                Assert.That(_blockTrees[nodeKey].Head.ParentHash, Is.EqualTo(hash), nodeKey.Address + " head parent hash");
                return this;
            }

            public On AssertHeadBlockIs(PrivateKey nodeKey, long number)
            {
                WaitForNumber(nodeKey, number);
                if (_logger.IsInfo) _logger.Info($"ASSERTING HEAD BLOCK IS BLOCK {number} ON {nodeKey.Address}");
                Assert.That(_blockTrees[nodeKey].Head.Number, Is.EqualTo(number), nodeKey.Address + " head number");
                return this;
            }

            public On AssertTransactionCount(PrivateKey nodeKey, long number, int transactionCount)
            {
                WaitForNumber(nodeKey, number);
                if (_logger.IsInfo) _logger.Info($"ASSERTING HEAD BLOCK IS BLOCK {number} ON {nodeKey.Address}");
                Assert.That(_blockTrees[nodeKey].Head.Transactions.Length, Is.EqualTo(transactionCount), nodeKey.Address + $" transaction count should be equal {transactionCount} for block number {number}");
                return this;
            }

            public On AssertHeadBlockTimestamp(PrivateKey nodeKey)
            {
                if (_logger.IsInfo) _logger.Info($"ASSERTING HEAD BLOCK TIMESTAMP ON {nodeKey.Address}");
                Assert.LessOrEqual(_blockTrees[nodeKey].FindBlock(_blockTrees[nodeKey].Head.Number - 1, BlockTreeLookupOptions.None).Timestamp + _cliqueConfig.BlockPeriod, _blockTrees[nodeKey].Head.Timestamp + 1);
                return this;
            }

            public On AssertVote(PrivateKey nodeKey, long number, Address address, bool vote)
            {
                WaitForNumber(nodeKey, number);
                if (_logger.IsInfo) _logger.Info($"ASSERTING {vote} VOTE ON {address} AT BLOCK {number}");
                Assert.That(_blockTrees[nodeKey].FindBlock(number, BlockTreeLookupOptions.None).Header.Nonce, Is.EqualTo(vote ? Consensus.Clique.Clique.NonceAuthVote : Consensus.Clique.Clique.NonceDropVote), nodeKey + " vote nonce");
                Assert.That(_blockTrees[nodeKey].FindBlock(number, BlockTreeLookupOptions.None).Beneficiary, Is.EqualTo(address), nodeKey.Address + " vote nonce");
                return this;
            }

            public On AssertSignersCount(PrivateKey nodeKey, long number, int count)
            {
                WaitForNumber(nodeKey, number);
                if (_logger.IsInfo) _logger.Info($"ASSERTING {count} SIGNERS AT BLOCK {number}");
                BlockHeader header = _blockTrees[nodeKey].FindBlock(number, BlockTreeLookupOptions.None).Header;
                Assert.That(_snapshotManager[nodeKey].GetOrCreateSnapshot(header.Number, header.Hash).Signers.Count, Is.EqualTo(count), nodeKey + " signers count");
                return this;
            }


            public On AssertTallyEmpty(PrivateKey nodeKey, long number, PrivateKey privateKeyB)
            {
                WaitForNumber(nodeKey, number);
                if (_logger.IsInfo) _logger.Info($"ASSERTING EMPTY TALLY FOR {privateKeyB.Address} EMPTY AT {number}");
                BlockHeader header = _blockTrees[nodeKey].FindBlock(number, BlockTreeLookupOptions.None).Header;
                Assert.That(_snapshotManager[nodeKey].GetOrCreateSnapshot(header.Number, header.Hash).Tally.ContainsKey(privateKeyB.Address), Is.EqualTo(false), nodeKey + " tally empty");
                return this;
            }

            public On AssertOutOfTurn(PrivateKey nodeKey, long number)
            {
                WaitForNumber(nodeKey, number);
                if (_logger.IsInfo) _logger.Info($"ASSERTING OUT TURN ON AT {nodeKey.Address} EMPTY AT BLOCK {number}");
                Assert.That(_blockTrees[nodeKey].Head.Difficulty, Is.EqualTo(Consensus.Clique.Clique.DifficultyNoTurn), nodeKey.Address + $" {number} out of turn");
                return this;
            }

            public On AssertInTurn(PrivateKey nodeKey, long number)
            {
                WaitForNumber(nodeKey, number);
                if (_logger.IsInfo) _logger.Info($"ASSERTING IN TURN ON AT {nodeKey.Address} EMPTY AT BLOCK {number}");
                Assert.That(_blockTrees[nodeKey].Head.Difficulty, Is.EqualTo(Consensus.Clique.Clique.DifficultyInTurn), nodeKey.Address + $" {number} in turn");
                return this;
            }

            private void WaitForNumber(PrivateKey nodeKey, long number)
            {
                if (_logger.IsInfo) _logger.Info($"WAITING ON {nodeKey.Address} FOR BLOCK {number}");
                SpinWait spinWait = new();
                Stopwatch stopwatch = new();
                stopwatch.Start();
                while (stopwatch.ElapsedMilliseconds < _timeout)
                {
                    spinWait.SpinOnce();
                    if (_blockTrees[nodeKey].Head.Number >= number)
                    {
                        break;
                    }
                }
            }

            public Block GetBlock(PrivateKey privateKey, long number)
            {
                Block block = _blockTrees[privateKey].FindBlock(number, BlockTreeLookupOptions.None);
                if (block is null)
                {
                    throw new InvalidOperationException($"Cannot find block {number}");
                }

                return block;
            }

            public async Task<On> StopNode(PrivateKey privateKeyA)
            {
                if (_logger.IsInfo) _logger.Info($"STOPPING {privateKeyA.Address}");
                await _producers[privateKeyA].StopAsync();
                return this;
            }

            private UInt256 _currentNonce = 0;

            public On AddPendingTransaction(PrivateKey nodeKey)
            {
                Transaction transaction = new();
                transaction.Value = 1;
                transaction.To = TestItem.AddressC;
                transaction.GasLimit = 30000;
                transaction.GasPrice = 20.GWei();
                transaction.Nonce = _currentNonce + 1;
                transaction.SenderAddress = TestItem.PrivateKeyD.Address;
                transaction.Hash = transaction.CalculateHash();
                _ethereumEcdsa.Sign(TestItem.PrivateKeyD, transaction, true);
                _pools[nodeKey].SubmitTx(transaction, TxHandlingOptions.None);

                return this;
            }

            public On AddAllBadTransactions(PrivateKey nodeKey)
            {
                // 0 gas price
                Transaction transaction = new();
                transaction.Value = 1;
                transaction.To = TestItem.AddressC;
                transaction.GasLimit = 30000;
                transaction.GasPrice = 0.GWei();
                transaction.Nonce = _currentNonce;
                transaction.SenderAddress = TestItem.PrivateKeyD.Address;
                transaction.Hash = transaction.CalculateHash();
                _ethereumEcdsa.Sign(TestItem.PrivateKeyD, transaction, true);
                _pools[nodeKey].SubmitTx(transaction, TxHandlingOptions.None);

                // bad nonce
                transaction = new Transaction();
                transaction.Value = 1;
                transaction.To = TestItem.AddressC;
                transaction.GasLimit = 30000;
                transaction.GasPrice = 20.GWei();
                transaction.Nonce = 0;
                transaction.SenderAddress = TestItem.PrivateKeyD.Address;
                transaction.Hash = transaction.CalculateHash();
                _ethereumEcdsa.Sign(TestItem.PrivateKeyD, transaction, true);
                _pools[nodeKey].SubmitTx(transaction, TxHandlingOptions.None);

                // gas limit too high
                transaction = new Transaction();
                transaction.Value = 1;
                transaction.To = TestItem.AddressC;
                transaction.GasLimit = 100000000;
                transaction.GasPrice = 20.GWei();
                transaction.Nonce = _currentNonce;
                transaction.SenderAddress = TestItem.PrivateKeyD.Address;
                transaction.Hash = transaction.CalculateHash();
                _ethereumEcdsa.Sign(TestItem.PrivateKeyD, transaction, true);
                _pools[nodeKey].SubmitTx(transaction, TxHandlingOptions.None);

                // insufficient balance
                transaction = new Transaction();
                transaction.Value = 1000000000.Ether();
                transaction.To = TestItem.AddressC;
                transaction.GasLimit = 30000;
                transaction.GasPrice = 20.GWei();
                transaction.Nonce = _currentNonce;
                transaction.SenderAddress = TestItem.PrivateKeyD.Address;
                transaction.Hash = transaction.CalculateHash();
                _ethereumEcdsa.Sign(TestItem.PrivateKeyD, transaction, true);
                _pools[nodeKey].SubmitTx(transaction, TxHandlingOptions.None);

                return this;
            }

            public On AddTransactionWithGasLimitToHigh(PrivateKey nodeKey)
            {
                Transaction transaction = new();

                // gas limit too high
                transaction = new Transaction();
                transaction.Value = 1;
                transaction.To = TestItem.AddressC;
                transaction.GasLimit = 100000000;
                transaction.GasPrice = 20.GWei();
                transaction.Nonce = _currentNonce;
                transaction.Data = Bytes.FromHexString("0xEF");
                transaction.SenderAddress = TestItem.PrivateKeyD.Address;
                transaction.Hash = transaction.CalculateHash();
                _ethereumEcdsa.Sign(TestItem.PrivateKeyD, transaction, true);
                _pools[nodeKey].SubmitTx(transaction, TxHandlingOptions.None);

                return this;
            }

            public On AddQueuedTransaction(PrivateKey nodeKey)
            {
                Transaction transaction = new();
                transaction.Value = 1;
                transaction.To = TestItem.AddressC;
                transaction.GasLimit = 30000;
                transaction.GasPrice = 20.GWei();
                transaction.Nonce = _currentNonce + 1000;
                transaction.SenderAddress = TestItem.PrivateKeyD.Address;
                transaction.Hash = transaction.CalculateHash();
                _ethereumEcdsa.Sign(TestItem.PrivateKeyD, transaction, true);
                _pools[nodeKey].SubmitTx(transaction, TxHandlingOptions.None);

                return this;
            }

            public On Wait(int i)
            {
                if (_logger.IsInfo) _logger.Info($"WAIT {i}");
                Thread.Sleep(i);
                return this;
            }
        }

        private static int _timeout = 2000; // this has to cover block period of second + wiggle of up to 500ms * (signers - 1) + 100ms delay of the block readiness check

        [Test]
        public async Task Can_produce_block_with_transactions()
        {
            await On.Goerli
                .CreateNode(TestItem.PrivateKeyA)
                .AddPendingTransaction(TestItem.PrivateKeyA)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 1L)
                .StopNode(TestItem.PrivateKeyA);
        }

        [Test]
        public async Task IsProducingBlocks_returns_expected_results()
        {
            On result = await On.Goerli
                .CreateNode(TestItem.PrivateKeyA)
                .ProcessGenesis()
                .IsProducingBlocks(TestItem.PrivateKeyA, true, null)
                .StopNode(TestItem.PrivateKeyA);

            result
                .IsProducingBlocks(TestItem.PrivateKeyA, false, null);
        }

        [Test]
        public async Task When_producing_blocks_skips_queued_and_bad_transactions()
        {
            await On.Goerli
                .CreateNode(TestItem.PrivateKeyA)
                .AddPendingTransaction(TestItem.PrivateKeyA)
                .AddPendingTransaction(TestItem.PrivateKeyA)
                .AddPendingTransaction(TestItem.PrivateKeyA)
                .AddAllBadTransactions(TestItem.PrivateKeyA)
                .AddQueuedTransaction(TestItem.PrivateKeyA)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 1)
                .StopNode(TestItem.PrivateKeyA);
        }

        [Test]
        public async Task Transaction_with_gas_limit_higher_than_block_gas_limit_should_not_be_send()
        {
            await On.Goerli
                .CreateNode(TestItem.PrivateKeyA)
                .AddTransactionWithGasLimitToHigh(TestItem.PrivateKeyA)
                .ProcessGenesis()
                .AssertTransactionCount(TestItem.PrivateKeyA, 1, 0)
                .StopNode(TestItem.PrivateKeyA);
        }

        [Test]
        public async Task Produces_block_on_top_of_genesis()
        {
            await On.Goerli
                .CreateNode(TestItem.PrivateKeyA)
                .CreateNode(TestItem.PrivateKeyB)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 1)
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 1)
                .AssertInTurn(TestItem.PrivateKeyA, 1)
                .AssertOutOfTurn(TestItem.PrivateKeyB, 1)
                .StopNode(TestItem.PrivateKeyA)
                .ContinueWith(t => t.Result.StopNode(TestItem.PrivateKeyB));
        }

        [Test]
        public void Single_validator_can_produce_first_block_in_turn()
        {
            On.Goerli
                .CreateNode(TestItem.PrivateKeyA)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 1)
                .AssertInTurn(TestItem.PrivateKeyA, 1);
        }

        [Test]
        public async Task Single_validator_can_produce_first_block_out_of_turn()
        {
            await On.Goerli
                .CreateNode(TestItem.PrivateKeyB)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 1)
                .AssertOutOfTurn(TestItem.PrivateKeyB, 1)
                .StopNode(TestItem.PrivateKeyB);
        }

        [Test]
        public async Task Cannot_produce_blocks_when_not_on_signers_list()
        {
            await On.Goerli
                .CreateNode(TestItem.PrivateKeyC)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestItem.PrivateKeyC, 0)
                .StopNode(TestItem.PrivateKeyC);
        }

        [Test]
        public async Task Can_cast_vote_to_include()
        {
            await On.Goerli
                .CreateNode(TestItem.PrivateKeyA)
                .VoteToInclude(TestItem.PrivateKeyA, TestItem.AddressC)
                .ProcessGenesis()
                .AssertVote(TestItem.PrivateKeyA, 1, TestItem.AddressC, true)
                .StopNode(TestItem.PrivateKeyA);
        }

        [Test]
        public async Task Can_uncast_vote_to()
        {
            await On.Goerli
                .CreateNode(TestItem.PrivateKeyA)
                .VoteToInclude(TestItem.PrivateKeyA, TestItem.AddressC)
                .UncastVote(TestItem.PrivateKeyA, TestItem.AddressC)
                .ProcessGenesis()
                .AssertVote(TestItem.PrivateKeyA, 1, Address.Zero, false)
                .StopNode(TestItem.PrivateKeyA);
        }

        [Test]
        public async Task Can_vote_a_validator_in()
        {
            On goerli = On.FastGoerli;
            goerli
                .CreateNode(TestItem.PrivateKeyA)
                .CreateNode(TestItem.PrivateKeyB)
                .CreateNode(TestItem.PrivateKeyC)
                .VoteToInclude(TestItem.PrivateKeyB, TestItem.AddressD)
                .ProcessGenesis3Validators()
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 1)
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 1)
                .AssertHeadBlockIs(TestItem.PrivateKeyC, 1)
                .VoteToInclude(TestItem.PrivateKeyA, TestItem.AddressD)
                .Process(TestItem.PrivateKeyA, goerli.GetBlock(TestItem.PrivateKeyB, 1))
                .Process(TestItem.PrivateKeyC, goerli.GetBlock(TestItem.PrivateKeyB, 1))
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 2)
                .AssertHeadBlockIs(TestItem.PrivateKeyC, 2)
                .Process(TestItem.PrivateKeyB, goerli.GetBlock(TestItem.PrivateKeyA, 2))
                .Process(TestItem.PrivateKeyC, goerli.GetBlock(TestItem.PrivateKeyA, 2))
                .Wait(1000)
                .AssertSignersCount(TestItem.PrivateKeyC, 2, 4);

            await goerli.StopNode(TestItem.PrivateKeyA);
            await goerli.StopNode(TestItem.PrivateKeyB);
            await goerli.StopNode(TestItem.PrivateKeyC);
        }

        [Test, Retry(3)]
        public async Task Can_vote_a_validator_out()
        {
            On goerli = On.FastGoerli;
            goerli
                .CreateNode(TestItem.PrivateKeyA)
                .CreateNode(TestItem.PrivateKeyB)
                .CreateNode(TestItem.PrivateKeyC)
                .VoteToExclude(TestItem.PrivateKeyA, TestItem.AddressC)
                .VoteToExclude(TestItem.PrivateKeyA, TestItem.AddressB)
                .ProcessGenesis3Validators()
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 1)
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 1)
                .AssertHeadBlockIs(TestItem.PrivateKeyC, 1)
                .Process(TestItem.PrivateKeyA, goerli.GetBlock(TestItem.PrivateKeyB, 1))
                .Process(TestItem.PrivateKeyC, goerli.GetBlock(TestItem.PrivateKeyB, 1))
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 2)
                .AssertHeadBlockIs(TestItem.PrivateKeyC, 2)
                .Process(TestItem.PrivateKeyB, goerli.GetBlock(TestItem.PrivateKeyA, 2))
                .Process(TestItem.PrivateKeyC, goerli.GetBlock(TestItem.PrivateKeyA, 2))
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 3)
                .AssertHeadBlockIs(TestItem.PrivateKeyC, 3)
                .Process(TestItem.PrivateKeyA, goerli.GetBlock(TestItem.PrivateKeyC, 3))
                .Process(TestItem.PrivateKeyB, goerli.GetBlock(TestItem.PrivateKeyC, 3))
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 4)
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 4)
                .VoteToExclude(TestItem.PrivateKeyB, TestItem.AddressA)
                .VoteToExclude(TestItem.PrivateKeyC, TestItem.AddressA)
                .Process(TestItem.PrivateKeyA, goerli.GetBlock(TestItem.PrivateKeyB, 4))
                .Process(TestItem.PrivateKeyC, goerli.GetBlock(TestItem.PrivateKeyB, 4))
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 5)
                .AssertHeadBlockIs(TestItem.PrivateKeyC, 5)
                .Process(TestItem.PrivateKeyB, goerli.GetBlock(TestItem.PrivateKeyA, 5))
                .Process(TestItem.PrivateKeyC, goerli.GetBlock(TestItem.PrivateKeyA, 5))
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 6)
                .AssertHeadBlockIs(TestItem.PrivateKeyC, 6)
                .Process(TestItem.PrivateKeyA, goerli.GetBlock(TestItem.PrivateKeyC, 6))
                .Process(TestItem.PrivateKeyB, goerli.GetBlock(TestItem.PrivateKeyC, 6))
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 6)
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 7)
                .Process(TestItem.PrivateKeyA, goerli.GetBlock(TestItem.PrivateKeyB, 7))
                .Process(TestItem.PrivateKeyC, goerli.GetBlock(TestItem.PrivateKeyB, 7))
                .Wait(1000)
                .AssertSignersCount(TestItem.PrivateKeyA, 7, 2)
                .AssertTallyEmpty(TestItem.PrivateKeyA, 7, TestItem.PrivateKeyB)
                .AssertTallyEmpty(TestItem.PrivateKeyA, 7, TestItem.PrivateKeyA)
                .AssertTallyEmpty(TestItem.PrivateKeyA, 7, TestItem.PrivateKeyC);

            await goerli.StopNode(TestItem.PrivateKeyA);
            await goerli.StopNode(TestItem.PrivateKeyB);
            await goerli.StopNode(TestItem.PrivateKeyC);
        }

        [Test]
        public async Task Can_cast_vote_to_exclude()
        {
            await On.Goerli
                .CreateNode(TestItem.PrivateKeyA)
                .VoteToExclude(TestItem.PrivateKeyA, TestItem.AddressB)
                .ProcessGenesis()
                .AssertVote(TestItem.PrivateKeyA, 1, TestItem.AddressB, false)
                .StopNode(TestItem.PrivateKeyA);
        }

        [Test]
        public async Task Cannot_vote_to_exclude_node_that_is_not_on_the_list()
        {
            await On.Goerli
                .CreateNode(TestItem.PrivateKeyA)
                .VoteToExclude(TestItem.PrivateKeyA, TestItem.AddressC)
                .ProcessGenesis()
                .AssertVote(TestItem.PrivateKeyA, 1, Address.Zero, false)
                .StopNode(TestItem.PrivateKeyA);
        }

        [Test]
        public async Task Cannot_vote_to_include_node_that_is_already_on_the_list()
        {
            await On.Goerli
                .CreateNode(TestItem.PrivateKeyA)
                .VoteToInclude(TestItem.PrivateKeyA, TestItem.AddressB)
                .ProcessGenesis()
                .AssertVote(TestItem.PrivateKeyA, 1, Address.Zero, false)
                .StopNode(TestItem.PrivateKeyA);
        }

        [Test]
        public async Task Can_reorganize_when_receiving_in_turn_blocks()
        {
            On goerli = On.FastGoerli;
            goerli
                .CreateNode(TestItem.PrivateKeyB)
                .CreateNode(TestItem.PrivateKeyA)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 1)
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 1)
                .Process(TestItem.PrivateKeyB, goerli.GetBlock(TestItem.PrivateKeyA, 1))
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 2);

            await goerli.StopNode(TestItem.PrivateKeyA);
            await goerli.StopNode(TestItem.PrivateKeyB);
        }

        [Test]
        public async Task Ignores_blocks_from_bad_network()
        {
            On goerli = On.FastGoerli;
            goerli
                .CreateNode(TestItem.PrivateKeyB)
                .ProcessGenesis(TestItem.PrivateKeyB)
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 1)
                .CreateNode(TestItem.PrivateKeyA)
                .ProcessBadGenesis(TestItem.PrivateKeyA)
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 1);

            Assert.That(goerli.GetBlock(TestItem.PrivateKeyB, 0).Hash, Is.Not.EqualTo(goerli.GetBlock(TestItem.PrivateKeyA, 0).Hash), "same genesis");

            goerli
                .Process(TestItem.PrivateKeyB, goerli.GetBlock(TestItem.PrivateKeyA, 1))
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 1);

            await goerli.StopNode(TestItem.PrivateKeyA);
            await goerli.StopNode(TestItem.PrivateKeyB);
        }

        [Test]
        public async Task Waits_for_block_timestamp_before_broadcasting()
        {
            On goerli = On.Goerli;
            goerli
                .CreateNode(TestItem.PrivateKeyB)
                .CreateNode(TestItem.PrivateKeyA)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 1)
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 1);

            Assert.That(goerli.GetBlock(TestItem.PrivateKeyB, 0).Hash, Is.EqualTo(goerli.GetBlock(TestItem.PrivateKeyA, 0).Hash), "same genesis");
            goerli
                .Process(TestItem.PrivateKeyB, goerli.GetBlock(TestItem.PrivateKeyA, 1))
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 1);

            await goerli.StopNode(TestItem.PrivateKeyA);
            await goerli.StopNode(TestItem.PrivateKeyB);
        }

        [Test]
        [Retry(3)]
        public async Task Creates_blocks_without_signals_from_block_tree()
        {
            await On.Goerli
                .CreateNode(TestItem.PrivateKeyA, true)
                .AssertHeadBlockIs(TestItem.PrivateKeyA, 1)
                .StopNode(TestItem.PrivateKeyA);

            await On.Goerli
                .CreateNode(TestItem.PrivateKeyB, true)
                .AssertHeadBlockIs(TestItem.PrivateKeyB, 1)
                .StopNode(TestItem.PrivateKeyB);
        }

        [Test]
        public async Task Can_stop()
        {
            On goerli = On.Goerli
                .CreateNode(TestItem.PrivateKeyA);

            await goerli.StopNode(TestItem.PrivateKeyA);

            goerli.ProcessGenesis();
            await Task.Delay(1000);
            goerli.AssertHeadBlockIs(TestItem.PrivateKeyA, 0);

            await goerli.StopNode(TestItem.PrivateKeyA);
        }

        [Test, Retry(3)]
        public async Task Many_validators_can_process_blocks()
        {
            PrivateKey[] keys = new[] { TestItem.PrivateKeyA, TestItem.PrivateKeyB, TestItem.PrivateKeyC }.OrderBy(pk => pk.Address, AddressComparer.Instance).ToArray();

            On goerli = On.FastGoerli;
            for (int i = 0; i < keys.Length; i++)
            {
                goerli
                    .CreateNode(keys[i])
                    .ProcessGenesis3Validators(keys[i])
                    .AssertHeadBlockIs(keys[i], 1);
            }

            for (int i = 1; i <= 10; i++)
            {
                PrivateKey inTurnKey = keys[i % 3];
                goerli.AddPendingTransaction(keys[(i + 1) % 3]);
                for (int j = 0; j < keys.Length; j++)
                {
                    PrivateKey nodeKey = keys[j];
                    if (!nodeKey.Equals(inTurnKey))
                    {
                        goerli.Process(nodeKey, goerli.GetBlock(inTurnKey, i));
                        goerli.AssertHeadBlockIs(keys[j], i + 1);
                        goerli.AssertHeadBlockTimestamp(keys[j]);
                    }
                    else
                    {
                        goerli.AssertHeadBlockIs(keys[j], i);
                        goerli.AssertHeadBlockTimestamp(keys[j]);
                    }
                }
            }

            for (int i = 0; i < keys.Length; i++)
            {
                await goerli.StopNode(keys[i]);
            }
        }
    }
}
