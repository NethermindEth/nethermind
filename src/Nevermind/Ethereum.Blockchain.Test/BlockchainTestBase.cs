using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
using Ethereum.VM.Test;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Signing;
using Nevermind.Core.Sugar;
using Nevermind.Evm;
using Nevermind.Store;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    public class BlockchainTestBase
    {
        private IBlockhashProvider _blockhashProvider;
        private InMemoryDb _db;
        private IWorldStateProvider _stateProvider;
        private IStorageProvider _storageProvider;
        private IVirtualMachine _virtualMachine;

        [SetUp]
        public void Setup()
        {
            _db = new InMemoryDb();
            _storageProvider = new TestStorageProvider(_db);
            _blockhashProvider = new TestBlockhashProvider();
            _stateProvider = new WorldStateProvider(new StateTree(_db));
            _virtualMachine = new VirtualMachine();
        }

        public static IEnumerable<BlockchainTest> LoadTests(string testSet)
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            IEnumerable<string> testDirs = Directory.EnumerateDirectories(".", "st" + testSet);
            Dictionary<string, Dictionary<string, BlockchainTestJson>> testJsons =
                new Dictionary<string, Dictionary<string, BlockchainTestJson>>();
            foreach (string testDir in testDirs)
            {
                testJsons[testDir] = new Dictionary<string, BlockchainTestJson>();
                IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir).ToList();
                foreach (string testFile in testFiles)
                {
                    string json = File.ReadAllText(testFile);
                    Dictionary<string, BlockchainTestJson> testsInFile =
                        JsonConvert.DeserializeObject<Dictionary<string, BlockchainTestJson>>(json);
                    foreach (KeyValuePair<string, BlockchainTestJson> namedTest in testsInFile)
                    {
                        testJsons[testDir].Add(namedTest.Key, namedTest.Value);
                    }
                }
            }

            if (testJsons.Any())
            {
                return testJsons.First().Value.Select(pair => Convert(pair.Key, pair.Value));
            }

            return new BlockchainTest[0];
        }

        private static AccountState Convert(AccountStateJson accountStateJson)
        {
            AccountState state = new AccountState();
            state.Balance = Hex.ToBytes(accountStateJson.Balance).ToUnsignedBigInteger();
            state.Code = Hex.ToBytes(accountStateJson.Code);
            state.Nonce = Hex.ToBytes(accountStateJson.Nonce).ToUnsignedBigInteger();
            state.Storage = accountStateJson.Storage.ToDictionary(
                p => Hex.ToBytes(p.Key).ToUnsignedBigInteger(),
                p => Hex.ToBytes(p.Value));
            return state;
        }

        protected void RunTest(BlockchainTest test)
        {
            foreach (KeyValuePair<Address, AccountState> accountState in test.Pre)
            {
                StorageTree storageTree = _storageProvider.GetOrCreateStorage(accountState.Key);
                foreach (KeyValuePair<BigInteger, byte[]> storageItem in accountState.Value.Storage)
                {
                    storageTree.Set(storageItem.Key, storageItem.Value);
                }

                _stateProvider.UpdateCode(accountState.Value.Code);

                Account account = new Account();
                account.Balance = accountState.Value.Balance;
                account.Nonce = accountState.Value.Nonce;
                account.StorageRoot = storageTree.RootHash;
                account.CodeHash = Keccak.Compute(accountState.Value.Code);
                _stateProvider.UpdateAccount(accountState.Key, account);
            }

            TransactionProcessor processor =
                new TransactionProcessor(_virtualMachine, _stateProvider, _storageProvider, ChainId.Mainnet,
                    false); // run twice depending on the EIP-155

            // TODO: handle multiple
            TestBlock oneBlock = test.Blocks[0];
            TestBlockHeader oneHeader = oneBlock.BlockHeader;
            IncomingTransaction oneTransaction = oneBlock.Transactions[0];

            Transaction transaction = new Transaction();
            transaction.To = oneTransaction.To;
            transaction.Value = oneTransaction.Value;
            transaction.GasLimit = oneTransaction.GasLimit;
            transaction.GasPrice = oneTransaction.GasPrice;
            transaction.Data = transaction.To == null ? null : oneTransaction.Data;
            transaction.Init = transaction.To == null ? oneTransaction.Data : null;
            transaction.Nonce = oneTransaction.Nonce;
            transaction.Signature = new Signature(oneTransaction.R, oneTransaction.S, oneTransaction.V);

            BlockHeader header = new BlockHeader();
            header.Number = oneHeader.Number;
            header.Difficulty = oneHeader.Difficulty;
            header.Timestamp = oneHeader.Timestamp;
            header.GasLimit = oneHeader.GasLimit;
            header.Beneficiary = oneHeader.Coinbase;
            header.GasUsed = oneHeader.GasUsed;
            header.MixHash = oneHeader.MixHash;
            header.ParentHash = oneHeader.ParentHash;
            header.OmmersHash = oneHeader.UncleHash;
            header.ReceiptsRoot = oneHeader.ReceiptTrie;
            header.TransactionsRoot = oneHeader.TransactionsTrie;
            header.ExtraData = oneHeader.ExtraData;
            header.StateRoot = oneHeader.StateRoot;
            header.LogsBloom = oneHeader.Bloom;

            Address sender = Signer.Recover(transaction);
            TransactionReceipt receipt = processor.Execute(
                sender,
                transaction,
                header,
                BigInteger.Zero
            );

            foreach (KeyValuePair<Address, AccountState> accountState in test.PostState)
            {
                StorageTree accountStorage = _storageProvider.GetOrCreateStorage(accountState.Key);
                foreach (KeyValuePair<BigInteger, byte[]> storageItem in accountState.Value.Storage)
                {
                    byte[] value = accountStorage.Get(storageItem.Key);
                    Assert.True(Bytes.UnsafeCompare(storageItem.Value, value),
                        $"Storage[{storageItem.Key}] Exp: {Hex.FromBytes(storageItem.Value, true)} != Actual: {Hex.FromBytes(value, true)}");
                }
            }

            Assert.AreEqual(header.GasUsed, receipt.GasUsed);

            Keccak receiptsRoot = BlockProcessor.GetReceiptsRoot(new[] { receipt });
            Keccak transactionsRoot = BlockProcessor.GetTransactionsRoot(new[] { transaction });
            Assert.AreEqual(header.TransactionsRoot, transactionsRoot);
            Assert.AreEqual(header.ReceiptsRoot, receiptsRoot);
            Assert.AreEqual(oneHeader.StateRoot, _stateProvider.State.RootHash);
        }

        private static TestBlockHeader Convert(TestBlockHeaderJson headerJson)
        {
            TestBlockHeader header = new TestBlockHeader();            
            header.Coinbase = new Address(headerJson.Coinbase);
            header.Bloom = new Bloom(); // TODO: bloom from string
            header.Difficulty = Hex.ToBytes(headerJson.Difficulty).ToUnsignedBigInteger();
            header.ExtraData = Hex.ToBytes(headerJson.ExtraData);
            header.GasLimit = Hex.ToBytes(headerJson.GasLimit).ToUnsignedBigInteger();
            header.GasUsed = Hex.ToBytes(headerJson.GasUsed).ToUnsignedBigInteger();
            header.Hash = new Keccak(headerJson.Hash);
            header.MixHash = new Keccak(headerJson.MixHash);
            header.Nonce = Hex.ToBytes(headerJson.Nonce).ToUnsignedBigInteger();
            header.Number = Hex.ToBytes(headerJson.Number).ToUnsignedBigInteger();
            header.ParentHash = new Keccak(headerJson.ParentHash);
            header.ReceiptTrie = new Keccak(headerJson.ReceiptTrie);
            header.StateRoot = new Keccak(headerJson.StateRoot);
            header.Timestamp = Hex.ToBytes(headerJson.Timestamp).ToUnsignedBigInteger();
            header.TransactionsTrie = new Keccak(headerJson.TransactionsTrie);
            header.UncleHash = new Keccak(headerJson.UncleHash);
            return header;
        }

        public class TestBlockHeaderJson
        {
            public string Bloom { get; set; }
            public string Coinbase { get; set; }
            public string Difficulty { get; set; }
            public string ExtraData { get; set; }
            public string GasLimit { get; set; }
            public string GasUsed { get; set; }
            public string Hash { get; set; }
            public string MixHash { get; set; }
            public string Nonce { get; set; }
            public string Number { get; set; }
            public string ParentHash { get; set; }
            public string ReceiptTrie { get; set; }
            public string StateRoot { get; set; }
            public string Timestamp { get; set; }
            public string TransactionsTrie { get; set; }
            public string UncleHash { get; set; }
        }

        public class TestBlockHeader
        {
            public Bloom Bloom { get; set; }
            public Address Coinbase { get; set; }
            public BigInteger Difficulty { get; set; }
            public byte[] ExtraData { get; set; }
            public BigInteger GasLimit { get; set; }
            public BigInteger GasUsed { get; set; }
            public Keccak Hash { get; set; }
            public Keccak MixHash { get; set; }
            public BigInteger Nonce { get; set; }
            public BigInteger Number { get; set; }
            public Keccak ParentHash { get; set; }
            public Keccak ReceiptTrie { get; set; }
            public Keccak StateRoot { get; set; }
            public BigInteger Timestamp { get; set; }
            public Keccak TransactionsTrie { get; set; }
            public Keccak UncleHash { get; set; }
        }

        private static TestBlock Convert(TestBlockJson testBlockJson)
        {
            TestBlock block = new TestBlock();
            block.BlockHeader = Convert(testBlockJson.BlockHeader);
            block.UncleHeaders = testBlockJson.UncleHeaders.Select(Convert).ToArray();
            block.Transactions = testBlockJson.Transactions.Select(Convert).ToArray();
            return block;
        }

        public class TestBlockJson
        {
            public TestBlockHeaderJson BlockHeader { get; set; }
            public TestBlockHeaderJson[] UncleHeaders { get; set; }
            public string Rlp { get; set; }
            public TransactionJson[] Transactions { get; set; }
        }

        public class TestBlock
        {
            public TestBlockHeader BlockHeader { get; set; }
            public TestBlockHeader[] UncleHeaders { get; set; }
            public string Rlp { get; set; }
            public IncomingTransaction[] Transactions { get; set; }
        }

        public class AccountState
        {
            public BigInteger Balance { get; set; }
            public byte[] Code { get; set; }
            public BigInteger Nonce { get; set; }
            public Dictionary<BigInteger, byte[]> Storage { get; set; }
        }

        public class AccountStateJson
        {
            public string Balance { get; set; }
            public string Code { get; set; }
            public string Nonce { get; set; }
            public Dictionary<string, string> Storage { get; set; }
        }

        private static IncomingTransaction Convert(TransactionJson transactionJson)
        {
            IncomingTransaction incomingTransaction = new IncomingTransaction();
            incomingTransaction.Data = Hex.ToBytes(transactionJson.Data[0]);
            incomingTransaction.Value = Hex.ToBytes(transactionJson.Value[0]).ToUnsignedBigInteger();
            incomingTransaction.GasLimit = Hex.ToBytes(transactionJson.GasLimit[0]).ToUnsignedBigInteger();
            incomingTransaction.GasPrice = Hex.ToBytes(transactionJson.GasPrice).ToUnsignedBigInteger();
            incomingTransaction.Nonce = Hex.ToBytes(transactionJson.Nonce).ToUnsignedBigInteger();
            incomingTransaction.To = string.IsNullOrWhiteSpace(transactionJson.To) ? null : new Address(new Hex(transactionJson.To));
            incomingTransaction.R = Hex.ToBytes(transactionJson.R);
            incomingTransaction.S = Hex.ToBytes(transactionJson.S);
            incomingTransaction.V = byte.Parse(transactionJson.V);
            return incomingTransaction;
        }

        public class TransactionJson
        {
            public string[] Data { get; set; }
            public string[] GasLimit { get; set; }
            public string GasPrice { get; set; }
            public string Nonce { get; set; }
            public string To { get; set; }
            public string[] Value { get; set; }
            public string R { get; set; }
            public string S { get; set; }
            public string V { get; set; }
        }

        public class IncomingTransaction
        {
            public byte[] Data { get; set; }
            public BigInteger GasLimit { get; set; }
            public BigInteger GasPrice { get; set; }
            public BigInteger Nonce { get; set; }
            public Address To { get; set; }
            public BigInteger Value { get; set; }
            public byte[] R { get; set; }
            public byte[] S { get; set; }
            public byte V { get; set; }
        }

        private static BlockchainTest Convert(string name, BlockchainTestJson testJson)
        {
            BlockchainTest test = new BlockchainTest();
            test.Name = name;
            EthereumNetwork network;
            Enum.TryParse(testJson.Network, true, out network);
            test.Network = network;
            test.LastBlockHash = new Keccak(testJson.LastBlockHash);
            test.GenesisRlp = new Rlp(Hex.ToBytes(testJson.GenesisRlp));
            test.GenesisBlockHeader = Convert(testJson.GenesisBlockHeader);
            test.Blocks = testJson.Blocks.Select(Convert).ToArray();
            test.PostState = testJson.PostState.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            test.Pre = testJson.Pre.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            return test;
        }

        public class BlockchainTestJson
        {
            public string Network { get; set; }
            public string LastBlockHash { get; set; }
            public string GenesisRlp { get; set; }

            public TestBlockJson[] Blocks { get; set; }
            public TestBlockHeaderJson GenesisBlockHeader { get; set; }

            public Dictionary<string, AccountStateJson> Pre { get; set; }
            public Dictionary<string, AccountStateJson> PostState { get; set; }
        }

        public class BlockchainTest
        {
            public string Name { get; set; }
            public EthereumNetwork Network { get; set; }
            public Keccak LastBlockHash { get; set; }
            public Rlp GenesisRlp { get; set; }

            public TestBlock[] Blocks { get; set; }
            public TestBlockHeader GenesisBlockHeader { get; set; }

            public Dictionary<Address, AccountState> Pre { get; set; }
            public Dictionary<Address, AccountState> PostState { get; set; }

            public override string ToString()
            {
                return Name;
            }
        }
    }
}