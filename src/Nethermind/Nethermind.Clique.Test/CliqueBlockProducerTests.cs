using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Test;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Blockchain.TransactionPools.Storages;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Clique.Test
{
    [TestFixture]
    public class CliqueBlockProducerTests
    {
        private class On
        {
            private static Timestamp _timestamp = new Timestamp(new DateTimeProvider());
            private CliqueConfig _cliqueConfig;
            private EthereumSigner _ethereumSigner = new EthereumSigner(GoerliSpecProvider.Instance, NullLogManager.Instance);
            private Dictionary<PrivateKey, BlockTree> _blockTrees = new Dictionary<PrivateKey, BlockTree>();
            private Dictionary<PrivateKey, AutoResetEvent> _blockEvents = new Dictionary<PrivateKey, AutoResetEvent>();
            private Dictionary<PrivateKey, CliqueBlockProducer> _producers = new Dictionary<PrivateKey, CliqueBlockProducer>();
            private Dictionary<PrivateKey, TransactionPool> _pools = new Dictionary<PrivateKey, TransactionPool>();

            private On()
                : this(15)
            {
            }

            private On(ulong blockPeriod)
            {
                _cliqueConfig = new CliqueConfig(blockPeriod, 30000);
                _genesis = GetGenesis();
                _genesis3Validators = GetGenesis(3);
            }

            public On CreateNode(PrivateKey privateKey, bool withGenesisAlreadyProcessed = false)
            {
                AutoResetEvent newHeadBlockEvent = new AutoResetEvent(false);
                _blockEvents.Add(privateKey, newHeadBlockEvent);

                MemDb blocksDb = new MemDb();
                MemDb blockInfoDb = new MemDb();

                TransactionPool transactionPool = new TransactionPool(new InMemoryTransactionStorage(), new PendingTransactionThresholdValidator(), _timestamp, _ethereumSigner, NullLogManager.Instance);
                _pools[privateKey] = transactionPool; 

                BlockTree blockTree = new BlockTree(blocksDb, blockInfoDb, GoerliSpecProvider.Instance, transactionPool, NullLogManager.Instance);
                blockTree.NewHeadBlock += (sender, args) => { _blockEvents[privateKey].Set(); };

                BlockhashProvider blockhashProvider = new BlockhashProvider(blockTree);
                _blockTrees.Add(privateKey, blockTree);

                EthereumSigner ethereumSigner = new EthereumSigner(GoerliSpecProvider.Instance, NullLogManager.Instance);
                CliqueSealEngine cliqueSealEngine = new CliqueSealEngine(_cliqueConfig, ethereumSigner, privateKey, new MemDb(), blockTree, NullLogManager.Instance);
                cliqueSealEngine.CanSeal = true;

                ISnapshotableDb stateDb = new StateDb();
                ISnapshotableDb codeDb = new StateDb();

                StateProvider stateProvider = new StateProvider(new StateTree(stateDb), codeDb, NullLogManager.Instance);
                stateProvider.CreateAccount(TestObject.PrivateKeyD.Address, 100.Ether());
                stateProvider.Commit(GoerliSpecProvider.Instance.GenesisSpec);

                _genesis.StateRoot = _genesis3Validators.StateRoot = stateProvider.StateRoot;
                _genesis.Hash = BlockHeader.CalculateHash(_genesis.Header);
                _genesis3Validators.Hash = BlockHeader.CalculateHash(_genesis3Validators.Header);
                
                StorageProvider storageProvider = new StorageProvider(stateDb, stateProvider, NullLogManager.Instance);
                TransactionProcessor transactionProcessor = new TransactionProcessor(GoerliSpecProvider.Instance, stateProvider, storageProvider, new VirtualMachine(stateProvider, storageProvider, blockhashProvider, NullLogManager.Instance), NullLogManager.Instance);
                BlockProcessor blockProcessor = new BlockProcessor(GoerliSpecProvider.Instance, TestBlockValidator.AlwaysValid, new NoBlockRewards(), transactionProcessor, stateDb, codeDb, stateProvider, storageProvider, transactionPool, NullReceiptStorage.Instance, NullLogManager.Instance);
                BlockchainProcessor processor = new BlockchainProcessor(blockTree, blockProcessor, new AuthorRecoveryStep(cliqueSealEngine), NullLogManager.Instance, false);
                processor.Start();

                StateProvider minerStateProvider = new StateProvider(new StateTree(stateDb), codeDb, NullLogManager.Instance);
                StorageProvider minerStorageProvider = new StorageProvider(stateDb, minerStateProvider, NullLogManager.Instance);
                VirtualMachine minerVirtualMachine = new VirtualMachine(minerStateProvider, minerStorageProvider, blockhashProvider, NullLogManager.Instance);
                TransactionProcessor minerTransactionProcessor = new TransactionProcessor(GoerliSpecProvider.Instance, minerStateProvider, minerStorageProvider, minerVirtualMachine, NullLogManager.Instance);
                BlockProcessor minerBlockProcessor = new BlockProcessor(GoerliSpecProvider.Instance, TestBlockValidator.AlwaysValid, new NoBlockRewards(), minerTransactionProcessor, stateDb, codeDb, minerStateProvider, minerStorageProvider, transactionPool, NullReceiptStorage.Instance, NullLogManager.Instance);
                BlockchainProcessor minerProcessor = new BlockchainProcessor(blockTree, minerBlockProcessor, new AuthorRecoveryStep(cliqueSealEngine), NullLogManager.Instance, false);

                if (withGenesisAlreadyProcessed)
                {
                    ProcessGenesis(privateKey);
                }
                
                CliqueBlockProducer blockProducer = new CliqueBlockProducer(transactionPool, minerProcessor, blockTree, minerStateProvider, _timestamp, new CryptoRandom(), cliqueSealEngine, _cliqueConfig, privateKey.Address, NullLogManager.Instance);
                blockProducer.Start();

                _producers.Add(privateKey, blockProducer);

                return this;
            }

            public static On Goerli => new On();

            public static On FastGoerli => new On(1);

            private Block _genesis3Validators;
            
            private Block _genesis;

            private Block GetGenesis(int validatorsCount = 2)
            {
                Keccak parentHash = Keccak.Zero;
                Keccak ommersHash = Keccak.OfAnEmptySequenceRlp;
                Address beneficiary = Address.Zero;
                UInt256 difficulty = new UInt256(1);
                UInt256 number = new UInt256(0);
                int gasLimit = 4700000;
                UInt256 timestamp = _timestamp.EpochSeconds - _cliqueConfig.BlockPeriod;
                string extraDataHex = "0x2249276d20646f6e652077616974696e672e2e2e20666f7220626c6f636b2066";
                extraDataHex += TestObject.PrivateKeyA.Address.ToString(false).Replace("0x", string.Empty);
                extraDataHex += TestObject.PrivateKeyB.Address.ToString(false).Replace("0x", string.Empty);
                if (validatorsCount > 2)
                {
                    extraDataHex += TestObject.PrivateKeyC.Address.ToString(false).Replace("0x", string.Empty);    
                }

                extraDataHex += "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
                
                byte[] extraData = Bytes.FromHexString(extraDataHex);
                BlockHeader header = new BlockHeader(parentHash, ommersHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData);
                Block genesis = new Block(header);
                genesis.Hash = BlockHeader.CalculateHash(genesis.Header);
                genesis.StateRoot = Keccak.EmptyTreeHash;
                genesis.TransactionsRoot = Keccak.EmptyTreeHash;
                genesis.Header.ReceiptsRoot = Keccak.EmptyTreeHash;

                return genesis;
            }

            public On VoteToInclude(PrivateKey nodeId, Address address)
            {
                _producers[nodeId].CastVote(address, true);
                return this;
            }

            public On UncastVote(PrivateKey nodeId, Address address)
            {
                _producers[nodeId].UncastVote(address);
                return this;
            }

            public On VoteToExclude(PrivateKey nodeId, Address address)
            {
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
                Thread.Sleep(1); // wait one second so the timestamp changes
                _blockTrees[nodeKey].SuggestBlock(GetGenesis());
                _blockEvents[nodeKey].WaitOne(_timeout);
                return this;
            }

            public On Process(PrivateKey nodeKey, Block block)
            {
                try
                {
                    _blockTrees[nodeKey].SuggestBlock(block);
                    _blockEvents[nodeKey].WaitOne(_timeout);
                    return this;
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            }

            public On AssertHeadBlockParentIs(PrivateKey nodeKey, Keccak hash)
            {
                Assert.AreEqual(hash, _blockTrees[nodeKey].Head.ParentHash, nodeKey.Address + " head parent hash");
                return this;
            }

            public On AssertHeadBlockIs(PrivateKey nodeKey, UInt256 number)
            {
                WaitForNumber(nodeKey, number);
                Assert.AreEqual(number, _blockTrees[nodeKey].Head.Number, nodeKey.Address + " head number");
                return this;
            }
            
            public On AssertTotalTxCount(PrivateKey nodeKey, int count)
            {
                Assert.AreEqual((UInt256)count, _blockTrees[nodeKey].Head.TotalTransactions, nodeKey.Address + " total tx count");
                return this;
            }
            
            public On AssertHeadBlockTimestamp(PrivateKey nodeKey)
            {
                Assert.LessOrEqual(_blockTrees[nodeKey].FindBlock(_blockTrees[nodeKey].Head.Number - 1).Timestamp + _cliqueConfig.BlockPeriod, _blockTrees[nodeKey].Head.Timestamp + 1);
                return this;
            }

            public On AssertVote(PrivateKey nodeKey, UInt256 number, Address address, bool vote)
            {
                WaitForNumber(nodeKey, number);
                Assert.AreEqual(vote ? Clique.NonceAuthVote : Clique.NonceDropVote, _blockTrees[nodeKey].FindBlock(number).Header.Nonce, nodeKey + " vote nonce");
                Assert.AreEqual(address, _blockTrees[nodeKey].FindBlock(number).Beneficiary, nodeKey.Address + " vote nonce");
                return this;
            }

            public On AssertOutOfTurn(PrivateKey nodeKey, UInt256 number)
            {
                WaitForNumber(nodeKey, number);
                Assert.AreEqual(Clique.DifficultyNoTurn, _blockTrees[nodeKey].Head.Difficulty, nodeKey.Address + $" {number} out of turn");
                return this;
            }

            public On AssertInTurn(PrivateKey nodeKey, UInt256 number)
            {
                WaitForNumber(nodeKey, number);
                Assert.AreEqual(Clique.DifficultyInTurn, _blockTrees[nodeKey].Head.Difficulty, nodeKey.Address + $" {number} in turn");
                return this;
            }

            private void WaitForNumber(PrivateKey nodeKey, UInt256 number)
            {
                SpinWait spinWait = new SpinWait();
                Stopwatch stopwatch = new Stopwatch();
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

            public Block GetBlock(PrivateKey privateKey, UInt256 number)
            {
                Block block = _blockTrees[privateKey].FindBlock(number);
                if (block == null)
                {
                    throw new InvalidOperationException($"Cannot find block {number}");
                }

                return block;
            }

            public async Task<On> StopNode(PrivateKey privateKeyA)
            {
                await _producers[privateKeyA].StopAsync();
                return this;
            }

            private UInt256 _currentNonce = 0;
            
            public On AddPendingTransaction(PrivateKey nodeKey)
            {
                Transaction transaction = new Transaction();
                transaction.Value = 1;
                transaction.To = TestObject.AddressC;
                transaction.GasLimit = 30000;
                transaction.GasPrice = 20.GWei();
                transaction.Nonce = _currentNonce++;
                transaction.SenderAddress = TestObject.PrivateKeyD.Address;
                transaction.Hash = Transaction.CalculateHash(transaction);
                _ethereumSigner.Sign(TestObject.PrivateKeyD, transaction, 1);
                _pools[nodeKey].AddTransaction(transaction, 1);
                
                return this;
            }
            
            public On AddAllBadTransactions(PrivateKey nodeKey)
            {
                Transaction transaction = new Transaction();
                transaction.Value = 1;
                transaction.To = TestObject.AddressC;
                transaction.GasLimit = 30000;
                transaction.GasPrice = 0.GWei();
                transaction.Nonce = _currentNonce;
                transaction.SenderAddress = TestObject.PrivateKeyD.Address;
                transaction.Hash = Transaction.CalculateHash(transaction);
                _ethereumSigner.Sign(TestObject.PrivateKeyD, transaction, 1);
                _pools[nodeKey].AddTransaction(transaction, 1);
                
                transaction = new Transaction();
                transaction.Value = 1;
                transaction.To = TestObject.AddressC;
                transaction.GasLimit = 30000;
                transaction.GasPrice = 20.GWei();
                transaction.Nonce = 0;
                transaction.SenderAddress = TestObject.PrivateKeyD.Address;
                transaction.Hash = Transaction.CalculateHash(transaction);
                _ethereumSigner.Sign(TestObject.PrivateKeyD, transaction, 1);
                _pools[nodeKey].AddTransaction(transaction, 1);
                
                transaction = new Transaction();
                transaction.Value = 1;
                transaction.To = TestObject.AddressC;
                transaction.GasLimit = 100000000;
                transaction.GasPrice = 20.GWei();
                transaction.Nonce = _currentNonce;
                transaction.SenderAddress = TestObject.PrivateKeyD.Address;
                transaction.Hash = Transaction.CalculateHash(transaction);
                _ethereumSigner.Sign(TestObject.PrivateKeyD, transaction, 1);
                _pools[nodeKey].AddTransaction(transaction, 1);
                
                return this;
            }
            
            public On AddQueuedTransaction(PrivateKey nodeKey)
            {
                Transaction transaction = new Transaction();
                transaction.Value = 1;
                transaction.To = TestObject.AddressC;
                transaction.GasLimit = 30000;
                transaction.GasPrice = 20.GWei();
                transaction.Nonce = _currentNonce + 1000;
                transaction.SenderAddress = TestObject.PrivateKeyD.Address;
                transaction.Hash = Transaction.CalculateHash(transaction);
                _ethereumSigner.Sign(TestObject.PrivateKeyD, transaction, 1);
                _pools[nodeKey].AddTransaction(transaction, 1);
                
                return this;
            }
        }

        private static int _timeout = 100000;

        [Test]
        public void Can_produce_block_with_transactions()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyA)
                .AddPendingTransaction(TestObject.PrivateKeyA)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestObject.PrivateKeyA, 1)
                .AssertTotalTxCount(TestObject.PrivateKeyA, 1);
        }
        
        [Test]
        public void When_producing_blocks_skips_queued_and_bad_transactions()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyA)
                .AddPendingTransaction(TestObject.PrivateKeyA)
                .AddPendingTransaction(TestObject.PrivateKeyA)
                .AddPendingTransaction(TestObject.PrivateKeyA)
                .AddAllBadTransactions(TestObject.PrivateKeyA)
                .AddQueuedTransaction(TestObject.PrivateKeyA)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestObject.PrivateKeyA, 1)
                .AssertTotalTxCount(TestObject.PrivateKeyA, 3);
        }
        
        [Test]
        public void Produces_block_on_top_of_genesis()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyA)
                .CreateNode(TestObject.PrivateKeyB)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestObject.PrivateKeyA, 1)
                .AssertHeadBlockIs(TestObject.PrivateKeyB, 1)
                .AssertInTurn(TestObject.PrivateKeyA, 1)
                .AssertOutOfTurn(TestObject.PrivateKeyB, 1);
        }

        [Test]
        public void Single_validator_can_produce_first_block_in_turn()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyA)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestObject.PrivateKeyA, 1)
                .AssertInTurn(TestObject.PrivateKeyA, 1);
        }

        [Test]
        public void Single_validator_can_produce_first_block_out_of_turn()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyB)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestObject.PrivateKeyB, 1)
                .AssertOutOfTurn(TestObject.PrivateKeyB, 1);
        }

        [Test]
        public void Cannot_produce_blocks_when_not_on_signers_list()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyC)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestObject.PrivateKeyC, 0);
        }

        [Test]
        public void Can_cast_vote_to_include()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyA)
                .VoteToInclude(TestObject.PrivateKeyA, TestObject.AddressC)
                .ProcessGenesis()
                .AssertVote(TestObject.PrivateKeyA, 1, TestObject.AddressC, true);
        }
        
        [Test]
        public void Can_uncast_vote_to()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyA)
                .VoteToInclude(TestObject.PrivateKeyA, TestObject.AddressC)
                .UncastVote(TestObject.PrivateKeyA, TestObject.AddressC)
                .ProcessGenesis()
                .AssertVote(TestObject.PrivateKeyA, 1, Address.Zero, false);
        }

        [Test]
        public void Can_cast_vote_to_exclude()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyA)
                .VoteToExclude(TestObject.PrivateKeyA, TestObject.AddressB)
                .ProcessGenesis()
                .AssertVote(TestObject.PrivateKeyA, 1, TestObject.AddressB, false);
        }

        [Test]
        public void Cannot_vote_to_exclude_node_that_is_not_on_the_list()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyA)
                .VoteToExclude(TestObject.PrivateKeyA, TestObject.AddressC)
                .ProcessGenesis()
                .AssertVote(TestObject.PrivateKeyA, 1, Address.Zero, false);
        }

        [Test]
        public void Cannot_vote_to_include_node_that_is_already_on_the_list()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyA)
                .VoteToInclude(TestObject.PrivateKeyA, TestObject.AddressB)
                .ProcessGenesis()
                .AssertVote(TestObject.PrivateKeyA, 1, Address.Zero, false);
        }

        [Test]
        public void Can_reorganize_when_receiving_in_turn_blocks()
        {
            var goerli = On.FastGoerli;
            goerli
                .CreateNode(TestObject.PrivateKeyB)
                .CreateNode(TestObject.PrivateKeyA)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestObject.PrivateKeyB, 1)
                .AssertHeadBlockIs(TestObject.PrivateKeyA, 1)
                .Process(TestObject.PrivateKeyB, goerli.GetBlock(TestObject.PrivateKeyA, 1))
                .AssertHeadBlockIs(TestObject.PrivateKeyB, 2);
        }
        
        [Test]
        public void Ignores_blocks_from_bad_network()
        {
            var goerli = On.FastGoerli;
            goerli
                .CreateNode(TestObject.PrivateKeyB)
                .ProcessGenesis(TestObject.PrivateKeyB)
                .AssertHeadBlockIs(TestObject.PrivateKeyB, 1)
                .CreateNode(TestObject.PrivateKeyA)
                .ProcessBadGenesis(TestObject.PrivateKeyA)
                .AssertHeadBlockIs(TestObject.PrivateKeyA, 1);

            Assert.AreNotEqual(goerli.GetBlock(TestObject.PrivateKeyA, 0).Hash, goerli.GetBlock(TestObject.PrivateKeyB, 0).Hash, "same genesis");

            goerli
                .Process(TestObject.PrivateKeyB, goerli.GetBlock(TestObject.PrivateKeyA, 1))
                .AssertHeadBlockIs(TestObject.PrivateKeyB, 1);
        }
        
        [Test]
        public void Waits_for_block_timestamp_before_broadcasting()
        {
            var goerli = On.Goerli;
            goerli
                .CreateNode(TestObject.PrivateKeyB)
                .CreateNode(TestObject.PrivateKeyA)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestObject.PrivateKeyB, 1)
                .AssertHeadBlockIs(TestObject.PrivateKeyA, 1);

            Assert.AreEqual(goerli.GetBlock(TestObject.PrivateKeyA, 0).Hash, goerli.GetBlock(TestObject.PrivateKeyB, 0).Hash, "same genesis");
            goerli
                .Process(TestObject.PrivateKeyB, goerli.GetBlock(TestObject.PrivateKeyA, 1))
                .AssertHeadBlockIs(TestObject.PrivateKeyB, 1);
        }
        
        [Test]
        public void Creates_blocks_without_signals_from_block_tree()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyA, true)
                .AssertHeadBlockIs(TestObject.PrivateKeyA, 1);
            
            On.Goerli
                .CreateNode(TestObject.PrivateKeyB, true)
                .AssertHeadBlockIs(TestObject.PrivateKeyB, 1);
        }
        
        [Test]
        public async Task Can_stop()
        {
            var goerli = On.Goerli
                .CreateNode(TestObject.PrivateKeyA);

            await goerli.StopNode(TestObject.PrivateKeyA);

            goerli.ProcessGenesis();
            await Task.Delay(1000);
            goerli.AssertHeadBlockIs(TestObject.PrivateKeyA, 0);
        }
        
        [Test]
        public void Many_validators_can_process_blocks()
        {
            PrivateKey[] keys = new [] {TestObject.PrivateKeyA, TestObject.PrivateKeyB, TestObject.PrivateKeyC}.OrderBy(pk => pk.Address, CliqueAddressComparer.Instance).ToArray();

            var goerli = On.FastGoerli;
            for (int i = 0; i < keys.Length; i++)
            {
                goerli
                    .CreateNode(keys[i])
                    .ProcessGenesis3Validators(keys[i])
                    .AssertHeadBlockIs(keys[i], 1);
            }

            for (int i = 1; i <= 10; i++)
            {
                var inTurnKey = keys[i % 3];
                goerli.AddPendingTransaction(keys[(i + 1) % 3]);
                for (int j = 0; j < keys.Length; j++)
                {
                    var nodeKey = keys[j]; 
                    if (!nodeKey.Equals(inTurnKey))
                    {
                        goerli.Process(nodeKey, goerli.GetBlock(inTurnKey, (UInt256)i));
                        goerli.AssertHeadBlockIs(keys[j], (UInt256)i + 1);
                        goerli.AssertHeadBlockTimestamp(keys[j]);
                    }
                    else
                    {
                        goerli.AssertHeadBlockIs(keys[j], (UInt256)i);
                        goerli.AssertHeadBlockTimestamp(keys[j]);
                    }
                }
            }
            
            goerli.AssertTotalTxCount(keys[0], 9);
        }
    }
}