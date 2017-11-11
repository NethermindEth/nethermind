using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
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
        private IProtocolSpecification _protocolSpecification = new FrontierProtocolSpecification();

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
                        if (!namedTest.Key.Contains("Frontier") && DateTime.Now < new DateTime(2017, 11, 15))
                        {
                            continue;
                        }

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

                _stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance);
                _stateProvider.UpdateStorageRoot(accountState.Key, storageTree.RootHash);
                Keccak codeHash = _stateProvider.UpdateCode(accountState.Value.Code);
                _stateProvider.UpdateCodeHash(accountState.Key, codeHash);
                for (int i = 0; i < accountState.Value.Nonce; i++)
                {
                    _stateProvider.IncrementNonce(accountState.Key);
                }
            }

            TransactionProcessor processor =
                new TransactionProcessor(_virtualMachine, _stateProvider, _storageProvider, _protocolSpecification, ChainId.Mainnet);

            // TODO: handle multiple
            TestBlock oneBlock = test.Blocks[0];
            TestBlockHeader oneHeader = oneBlock.BlockHeader;

            BlockHeader header = new BlockHeader();
            header.Number = oneHeader.Number;
            header.Difficulty = oneHeader.Difficulty;
            header.Timestamp = oneHeader.Timestamp;
            header.GasLimit = oneHeader.GasLimit;
            header.Beneficiary = oneHeader.Coinbase;
            header.GasUsed = 0;
            header.MixHash = oneHeader.MixHash;
            header.ParentHash = oneHeader.ParentHash;
            header.OmmersHash = oneHeader.UncleHash;
            header.ReceiptsRoot = oneHeader.ReceiptTrie;
            header.TransactionsRoot = oneHeader.TransactionsTrie;
            header.ExtraData = oneHeader.ExtraData;
            header.StateRoot = oneHeader.StateRoot;
            header.LogsBloom = oneHeader.Bloom;

            List<TransactionReceipt> receipts = new List<TransactionReceipt>();
            List<Transaction> transactions = new List<Transaction>();

            BigInteger gasUsedSoFar = 0;
            foreach (IncomingTransaction testTransaction in oneBlock.Transactions)
            {
                Transaction transaction = new Transaction();
                transaction.To = testTransaction.To;
                transaction.Value = testTransaction.Value;
                transaction.GasLimit = testTransaction.GasLimit;
                transaction.GasPrice = testTransaction.GasPrice;
                transaction.Data = transaction.To == null ? null : testTransaction.Data;
                transaction.Init = transaction.To == null ? testTransaction.Data : null;
                transaction.Nonce = testTransaction.Nonce;
                transaction.Signature = new Signature(testTransaction.R, testTransaction.S, testTransaction.V);
                transactions.Add(transaction);

                Address sender = Signer.Recover(transaction);
                TransactionReceipt receipt = processor.Execute(
                    sender,
                    transaction,
                    header,
                    gasUsedSoFar
                );

                receipts.Add(receipt);
                gasUsedSoFar += receipt.GasUsed;
            }

            if (!_stateProvider.AccountExists(header.Beneficiary))
            {
                _stateProvider.CreateAccount(header.Beneficiary, 0);
            }

            _stateProvider.UpdateBalance(header.Beneficiary, 5.Ether());

            List<string> differences = new List<string>();
            foreach (KeyValuePair<Address, AccountState> accountState in test.PostState)
            {
                bool accountExists = _stateProvider.AccountExists(accountState.Key);
                BigInteger? balance = accountExists ? _stateProvider.GetBalance(accountState.Key) : (BigInteger?)null;
                BigInteger? nonce = accountExists ? _stateProvider.GetNonce(accountState.Key) : (BigInteger?)null;

                if (accountState.Value.Balance != balance)
                {
                    differences.Add($"{accountState.Key} balance exp: {accountState.Value.Balance}, actual: {balance}");
                }

                if (accountState.Value.Nonce != nonce)
                {
                    differences.Add($"{accountState.Key} nonce exp: {accountState.Value.Nonce}, actual: {nonce}");
                }

                byte[] code = accountExists ? _stateProvider.GetCode(accountState.Key) : new byte[0];
                if (!Bytes.UnsafeCompare(accountState.Value.Code, code))
                {
                    differences.Add($"{accountState.Key} code exp: {accountState.Value.Code?.Length}, actual: {code?.Length}");
                }

                StorageTree accountStorage = _storageProvider.GetOrCreateStorage(accountState.Key);


                foreach (KeyValuePair<BigInteger, byte[]> storageItem in accountState.Value.Storage)
                {
                    byte[] value = accountStorage?.Get(storageItem.Key) ?? new byte[0];
                    if (!Bytes.UnsafeCompare(storageItem.Value, value))
                    {
                        differences.Add($"{accountState.Key} storage[{storageItem.Key}] exp: {Hex.FromBytes(storageItem.Value, true)}, actual: {Hex.FromBytes(value, true)}");
                    }
                }
            }

            foreach (string difference in differences)
            {
                Console.WriteLine(difference);
            }

            Assert.Zero(differences.Count, "differences");

            Assert.AreEqual(oneHeader.GasUsed, gasUsedSoFar);

            Keccak receiptsRoot = BlockProcessor.GetReceiptsRoot(receipts.ToArray());
            Keccak transactionsRoot = BlockProcessor.GetTransactionsRoot(transactions.ToArray());
            
            Assert.AreEqual(oneHeader.StateRoot, _stateProvider.State.RootHash, "state root");
            Assert.AreEqual(header.TransactionsRoot, transactionsRoot, "transactions root");
            Assert.AreEqual(header.ReceiptsRoot, receiptsRoot, "receipts root");
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
            incomingTransaction.Data = Hex.ToBytes(transactionJson.Data);
            incomingTransaction.Value = Hex.ToBytes(transactionJson.Value).ToUnsignedBigInteger();
            incomingTransaction.GasLimit = Hex.ToBytes(transactionJson.GasLimit).ToUnsignedBigInteger();
            incomingTransaction.GasPrice = Hex.ToBytes(transactionJson.GasPrice).ToUnsignedBigInteger();
            incomingTransaction.Nonce = Hex.ToBytes(transactionJson.Nonce).ToUnsignedBigInteger();
            incomingTransaction.To = string.IsNullOrWhiteSpace(transactionJson.To) ? null : new Address(new Hex(transactionJson.To));
            incomingTransaction.R = Hex.ToBytes(transactionJson.R).PadLeft(32);
            incomingTransaction.S = Hex.ToBytes(transactionJson.S).PadLeft(32);
            incomingTransaction.V = Hex.ToBytes(transactionJson.V)[0];
            return incomingTransaction;
        }

        public class TransactionJson
        {
            public string Data { get; set; }
            public string GasLimit { get; set; }
            public string GasPrice { get; set; }
            public string Nonce { get; set; }
            public string To { get; set; }
            public string Value { get; set; }
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