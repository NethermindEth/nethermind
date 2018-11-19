using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
            private CliqueConfig _cliqueConfig = new CliqueConfig(15, 30000);
            private Timestamp _timestamp = new Timestamp(new DateTimeProvider());
            private EthereumSigner _ethereumSigner = new EthereumSigner(GoerliSpecProvider.Instance, NullLogManager.Instance);
            private Dictionary<PrivateKey, BlockTree> _blockTrees = new Dictionary<PrivateKey, BlockTree>();
            private Dictionary<PrivateKey, AutoResetEvent> _blockEvents = new Dictionary<PrivateKey, AutoResetEvent>();

            private On()
            {
            }

            public On CreateNode(PrivateKey privateKey)
            {
                AutoResetEvent newHeadBlockEvent = new AutoResetEvent(false);
                _blockEvents.Add(privateKey, newHeadBlockEvent);

                MemDb blocksDb = new MemDb();
                MemDb blockInfoDb = new MemDb();

                TransactionPool transactionPool = new TransactionPool(new InMemoryTransactionStorage(), new PendingTransactionThresholdValidator(), _timestamp, _ethereumSigner, NullLogManager.Instance);

                BlockTree blockTree = new BlockTree(blocksDb, blockInfoDb, GoerliSpecProvider.Instance, transactionPool, NullLogManager.Instance);
                blockTree.NewHeadBlock += (sender, args) => _blockEvents[privateKey].Set(); 
                BlockhashProvider blockhashProvider = new BlockhashProvider(blockTree);
                _blockTrees.Add(privateKey, blockTree);

                EthereumSigner ethereumSigner = new EthereumSigner(GoerliSpecProvider.Instance, NullLogManager.Instance);
                CliqueSealEngine cliqueSealEngine = new CliqueSealEngine(_cliqueConfig, ethereumSigner, privateKey, new MemDb(), blockTree, NullLogManager.Instance);
                cliqueSealEngine.CanSeal = true;
                
                ISnapshotableDb stateDb = new StateDb();
                ISnapshotableDb codeDb = new StateDb();
                
                StateProvider stateProvider = new StateProvider(new StateTree(stateDb), codeDb, NullLogManager.Instance);
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

                CliqueBlockProducer blockProducer = new CliqueBlockProducer(transactionPool, minerProcessor, blockTree, minerStateProvider, _timestamp, new CryptoRandom(), cliqueSealEngine, _cliqueConfig, privateKey.Address, NullLogManager.Instance);
                blockProducer.Start();

                return this;
            }

            public static On Goerli => new On();

            private Block GetGenesis()
            {
                Keccak parentHash = Keccak.Zero;
                Keccak ommersHash = Keccak.OfAnEmptySequenceRlp;
                Address beneficiary = Address.Zero;
                UInt256 difficulty = new UInt256(1);
                UInt256 number = new UInt256(0);
                int gasLimit = 4700000;
                UInt256 timestamp = _timestamp.EpochSeconds - 15;
                byte[] extraData = Bytes.FromHexString("0x2249276d20646f6e652077616974696e672e2e2e20666f7220626c6f636b2066" + TestObject.PrivateKeyA.Address.ToString(false).Replace("0x", string.Empty) + TestObject.PrivateKeyB.Address.ToString(false).Replace("0x", string.Empty) + "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
                BlockHeader header = new BlockHeader(parentHash, ommersHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData);
                Block genesis = new Block(header);
                genesis.Hash = BlockHeader.CalculateHash(genesis.Header);
                genesis.StateRoot = Keccak.EmptyTreeHash;
                genesis.TransactionsRoot = Keccak.EmptyTreeHash;
                genesis.Header.ReceiptsRoot = Keccak.EmptyTreeHash;
                
                return genesis;
            }

            public On ProcessGenesis()
            {
                Block genesis = GetGenesis();
                foreach (KeyValuePair<PrivateKey,BlockTree> node in _blockTrees)
                {
                    _blockTrees[node.Key].SuggestBlock(genesis);
                    _blockEvents[node.Key].WaitOne();
                }

                return this;
            }
            
            public On ProcessGenesis(PrivateKey nodeKey)
            {
                _blockTrees[nodeKey].SuggestBlock(GetGenesis());
                _blockEvents[nodeKey].WaitOne(_timeout);
                return this;
            }

            public On AssertHeadBlockIs(PrivateKey nodeKey, UInt256 number)
            {
                WaitForNumber(nodeKey, number);
                Assert.AreEqual(number, _blockTrees[nodeKey].Head.Number, nodeKey + " head number");
                return this;
            }
            
            public On AssertOutOfTurn(PrivateKey nodeKey, UInt256 number)
            {
                WaitForNumber(nodeKey, number);
                Assert.AreEqual((UInt256)2, _blockTrees[nodeKey].Head.Difficulty, nodeKey + $" {number} out of turn");
                return this;
            }
            
            public On AssertInTurn(PrivateKey nodeKey, UInt256 number)
            {
                WaitForNumber(nodeKey, number);
                Assert.AreEqual(UInt256.One, _blockTrees[nodeKey].Head.Difficulty, nodeKey + $" {number} in turn");
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
        }

        private static int _timeout = 100000;
        
        [Test]
        public void Produces_block_on_top_of_genesis()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyA)
                .CreateNode(TestObject.PrivateKeyB)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestObject.PrivateKeyA, 1)
                .AssertHeadBlockIs(TestObject.PrivateKeyB, 1)
                .AssertOutOfTurn(TestObject.PrivateKeyA, 1)
                .AssertInTurn(TestObject.PrivateKeyB, 1);
        }
        
        [Test]
        public void Single_validator_can_produce_first_block_in_turn()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyA)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestObject.PrivateKeyA, 1)
                .AssertOutOfTurn(TestObject.PrivateKeyA, 1);
        }
        
        [Test]
        public void Single_validator_can_produce_first_block_out_of_turn()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyB)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestObject.PrivateKeyB, 1)
                .AssertInTurn(TestObject.PrivateKeyB, 1);
        }
        
        [Test]
        public void Cannot_produce_blocks_when_not_on_signers_list()
        {
            On.Goerli
                .CreateNode(TestObject.PrivateKeyC)
                .ProcessGenesis()
                .AssertHeadBlockIs(TestObject.PrivateKeyC, 0);
        }
    }
}