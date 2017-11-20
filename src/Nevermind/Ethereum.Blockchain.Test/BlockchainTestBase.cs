using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
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
        private readonly IProtocolSpecificationProvider _protocolSpecificationProvider = new ProtocolSpecificationProvider();
        private IBlockhashProvider _blockhashProvider;
        private InMemoryDb _db;
        private IStorageProvider _storageProvider;
        private Dictionary<EthereumNetwork, VirtualMachine> _virtualMachines;
        private Dictionary<EthereumNetwork, StateProvider> _stateProviders;
        private ILogger _logger;

        protected void Setup(ILogger logger)
        {
            _logger = logger;
            _db = new InMemoryDb();
            _storageProvider = new StorageProvider(ShouldLog.State ? logger : null);
            _blockhashProvider = new TestBlockhashProvider();
            _virtualMachines = new Dictionary<EthereumNetwork, VirtualMachine>();
            _stateProviders = new Dictionary<EthereumNetwork, StateProvider>();
            EthereumNetwork[] networks = { EthereumNetwork.Frontier, EthereumNetwork.Homestead, EthereumNetwork.Byzantium, EthereumNetwork.SpuriousDragon, EthereumNetwork.TangerineWhistle };
            foreach (EthereumNetwork ethereumNetwork in networks)
            {
                IProtocolSpecification spec = _protocolSpecificationProvider.GetSpec(ethereumNetwork, 1);
                _stateProviders[ethereumNetwork] = new StateProvider(new StateTree(_db), spec, ShouldLog.State ? logger : null);
                _virtualMachines[ethereumNetwork] = new VirtualMachine(_blockhashProvider, _stateProviders[ethereumNetwork], _storageProvider, spec, ShouldLog.Evm ? logger : null);
            }
        }

        [SetUp]
        public void Setup()
        {
            Setup(new ConsoleLogger());
        }

        public static IEnumerable<BlockchainTest> LoadTests(string testSet)
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            IEnumerable<string> testDirs = Directory.EnumerateDirectories(".", "st" + (testSet.StartsWith("st") ? testSet.Substring(2) : testSet));
            Dictionary<string, Dictionary<string, BlockchainTestJson>> testJsons = new Dictionary<string, Dictionary<string, BlockchainTestJson>>();
            foreach (string testDir in testDirs)
            {
                testJsons[testDir] = LoadTestsFromDirectory(testDir);
            }

            return testJsons.SelectMany(d => d.Value).Select(pair => Convert(pair.Key, pair.Value));
        }

        private static Dictionary<string, BlockchainTestJson> LoadTestsFromDirectory(string testDir)
        {
            Dictionary<string, BlockchainTestJson> testsByName = new Dictionary<string, BlockchainTestJson>();
            List<string> testFiles = Directory.EnumerateFiles(testDir).ToList();
            foreach (string testFile in testFiles)
            {
                string json = File.ReadAllText(testFile);
                Dictionary<string, BlockchainTestJson> testsInFile = JsonConvert.DeserializeObject<Dictionary<string, BlockchainTestJson>>(json);
                foreach (KeyValuePair<string, BlockchainTestJson> namedTest in testsInFile)
                {
                    if (namedTest.Key.Contains("Frontier"))
                    {
                        namedTest.Value.EthereumNetwork = EthereumNetwork.Frontier;
                    }
                    else
                    if (namedTest.Key.Contains("Homestead"))
                    {
                        namedTest.Value.EthereumNetwork = EthereumNetwork.Homestead;
                    }
                    else
                    if (namedTest.Key.Contains("EIP150"))
                    {
                        namedTest.Value.EthereumNetwork = EthereumNetwork.TangerineWhistle;
                    }
                    //else
                    //if (namedTest.Key.Contains("EIP158"))
                    //{
                    //    namedTest.Value.EthereumNetwork = EthereumNetwork.SpuriousDragon;
                    //}
                    //else
                    //if (namedTest.Key.Contains("Byzantium"))
                    //{
                    //    namedTest.Value.EthereumNetwork = EthereumNetwork.Byzantium;
                    //}
                    else
                    {
                        continue;
                    }

                    testsByName.Add(namedTest.Key, namedTest.Value);
                }
            }
            return testsByName;
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

        private class LoggingTraceListener : TraceListener
        {
            private readonly ILogger _logger;

            public LoggingTraceListener(ILogger logger)
            {
                _logger = logger;
            }

            private readonly StringBuilder _line = new StringBuilder();

            public override void Write(string message)
            {
                _line.Append(message);
            }

            public override void WriteLine(string message)
            {
                Write(message);
                _logger?.Log(_line.ToString());
                _line.Clear();
            }
        }

        private class LoggingConsole : TextWriter
        {
            private readonly TraceListener _traceListener;

            public LoggingConsole(TraceListener traceListener)
            {
                _traceListener = traceListener;
            }

            public override void Write(char value)
            {
                _traceListener.Write(value);
            }

            public override void WriteLine()
            {
                _traceListener.WriteLine("");
            }

            public override void WriteLine(string value)
            {
                _traceListener.WriteLine(value);
            }

            public override Task WriteAsync(char value)
            {
                _traceListener.Write(value);
                return Task.CompletedTask;
            }

            public override Encoding Encoding => Encoding.UTF8;
        }

        protected void RunTest(BlockchainTest test, Stopwatch stopwatch = null)
        {
            LoggingTraceListener traceListener = new LoggingTraceListener(_logger);
            Debug.Listeners.Clear();
            Debug.Listeners.Add(traceListener);

            TextWriter defaultWriterError = Console.Error;
            TextWriter defaultWriter = Console.Out;
            //Console.SetError(new LoggingConsole(traceListener));
            //Console.SetOut(new LoggingConsole(traceListener));

            try

            {
                foreach (KeyValuePair<Address, AccountState> accountState in test.Pre)
                {
                    foreach (KeyValuePair<BigInteger, byte[]> storageItem in accountState.Value.Storage)
                    {
                        _storageProvider.Set(accountState.Key, storageItem.Key, storageItem.Value);
                    }

                    _stateProviders[test.Network].CreateAccount(accountState.Key, accountState.Value.Balance);
                    Keccak codeHash = _stateProviders[test.Network].UpdateCode(accountState.Value.Code);
                    _stateProviders[test.Network].UpdateCodeHash(accountState.Key, codeHash);
                    for (int i = 0; i < accountState.Value.Nonce; i++)
                    {
                        _stateProviders[test.Network].IncrementNonce(accountState.Key);
                    }
                }

                _storageProvider.Commit(_stateProviders[test.Network]);
                _stateProviders[test.Network].Commit();

                IProtocolSpecification spec = _protocolSpecificationProvider.GetSpec(test.Network, 1);
                TransactionProcessor processor = new TransactionProcessor(_virtualMachines[test.Network], _stateProviders[test.Network], _storageProvider, spec, ChainId.Mainnet, ShouldLog.TransactionProcessor ? _logger : null);

                // TODO: handle multiple
                BlockHeader header = BuildBlockHeader(test.Blocks[0].BlockHeader);
                List<TransactionReceipt> receipts = new List<TransactionReceipt>();
                List<Transaction> transactions = new List<Transaction>();

                stopwatch?.Start();
                BigInteger gasUsedSoFar = 0;
                foreach (IncomingTransaction testTransaction in test.Blocks[0].Transactions)
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

                    TransactionReceipt receipt = processor.Execute(
                        transaction,
                        header,
                        gasUsedSoFar
                    );

                    receipts.Add(receipt);
                    gasUsedSoFar += receipt.GasUsed;
                }

                stopwatch?.Start();

                BigInteger reward = spec.IsEip186Enabled ? 3.Ether() : 5.Ether();
                if (!_stateProviders[test.Network].AccountExists(header.Beneficiary))
                {
                    _stateProviders[test.Network].CreateAccount(header.Beneficiary, reward);
                }
                else
                {
                    _stateProviders[test.Network].UpdateBalance(header.Beneficiary, reward);
                }

                _storageProvider.Commit(_stateProviders[test.Network]);
                _stateProviders[test.Network].Commit();

                RunAssertions(test, receipts, transactions);

            }
            finally
            {
                Console.SetError(defaultWriterError);
                Console.SetOut(defaultWriter);
            }
        }

        private void RunAssertions(BlockchainTest test, List<TransactionReceipt> receipts, List<Transaction> transactions)
        {
            TestBlockHeader testHeader = test.Blocks[0].BlockHeader;
            List<string> differences = new List<string>();
            foreach (KeyValuePair<Address, AccountState> accountState in test.PostState)
            {
                if (differences.Count > 8)
                {
                    Console.WriteLine("More than 8 differences...");
                    break;
                }

                bool accountExists = _stateProviders[test.Network].AccountExists(accountState.Key);
                BigInteger? balance = accountExists ? _stateProviders[test.Network].GetBalance(accountState.Key) : (BigInteger?)null;
                BigInteger? nonce = accountExists ? _stateProviders[test.Network].GetNonce(accountState.Key) : (BigInteger?)null;

                if (accountState.Value.Balance != balance)
                {
                    differences.Add($"{accountState.Key} balance exp: {accountState.Value.Balance}, actual: {balance}, diff: {accountState.Value.Balance - balance}");
                }

                if (accountState.Value.Nonce != nonce)
                {
                    differences.Add($"{accountState.Key} nonce exp: {accountState.Value.Nonce}, actual: {nonce}");
                }

                byte[] code = accountExists ? _stateProviders[test.Network].GetCode(accountState.Key) : new byte[0];
                if (!Bytes.UnsafeCompare(accountState.Value.Code, code))
                {
                    differences.Add($"{accountState.Key} code exp: {accountState.Value.Code?.Length}, actual: {code?.Length}");
                }

                foreach (KeyValuePair<BigInteger, byte[]> storageItem in accountState.Value.Storage)
                {
                    byte[] value = _storageProvider.Get(accountState.Key, storageItem.Key) ?? new byte[0];
                    if (!Bytes.UnsafeCompare(storageItem.Value, value))
                    {
                        differences.Add($"{accountState.Key} storage[{storageItem.Key}] exp: {Hex.FromBytes(storageItem.Value, true)}, actual: {Hex.FromBytes(value, true)}");
                    }
                }
            }

            foreach (string difference in differences)
            {
                _logger?.Log(difference);
            }

            Assert.Zero(differences.Count, "differences");

            Assert.AreEqual(testHeader.GasUsed, receipts.LastOrDefault()?.GasUsed ?? 0);
            Keccak receiptsRoot = BlockProcessor.GetReceiptsRoot(receipts.ToArray());
            Keccak transactionsRoot = BlockProcessor.GetTransactionsRoot(transactions.ToArray());

            if (receipts.Any())
            {
                Assert.AreEqual(testHeader.Bloom.ToString(), receipts.Last().Bloom.ToString(), "bloom");
            }

            Assert.AreEqual(testHeader.StateRoot, _stateProviders[test.Network].State.RootHash, "state root");
            Assert.AreEqual(testHeader.TransactionsTrie, transactionsRoot, "transactions root");
            Assert.AreEqual(testHeader.ReceiptTrie, receiptsRoot, "receipts root");
        }

        private static BlockHeader BuildBlockHeader(TestBlockHeader oneHeader)
        {
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
            return header;
        }

        private static TestBlockHeader Convert(TestBlockHeaderJson headerJson)
        {
            TestBlockHeader header = new TestBlockHeader();
            header.Coinbase = new Address(headerJson.Coinbase);
            header.Bloom = new Bloom(Hex.ToBytes(headerJson.Bloom).ToBigEndianBitArray2048());
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

        private static TestBlock Convert(TestBlockJson testBlockJson)
        {
            TestBlock block = new TestBlock();
            block.BlockHeader = Convert(testBlockJson.BlockHeader);
            block.UncleHeaders = testBlockJson.UncleHeaders.Select(Convert).ToArray();
            block.Transactions = testBlockJson.Transactions.Select(Convert).ToArray();
            return block;
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

        private static BlockchainTest Convert(string name, BlockchainTestJson testJson)
        {
            BlockchainTest test = new BlockchainTest();
            test.Name = name;
            test.Network = testJson.EthereumNetwork;
            test.LastBlockHash = new Keccak(testJson.LastBlockHash);
            test.GenesisRlp = new Rlp(Hex.ToBytes(testJson.GenesisRlp));
            test.GenesisBlockHeader = Convert(testJson.GenesisBlockHeader);
            test.Blocks = testJson.Blocks.Select(Convert).ToArray();
            test.PostState = testJson.PostState.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            test.Pre = testJson.Pre.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            return test;
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

        public class BlockchainTestJson
        {
            public string Network { get; set; }
            public EthereumNetwork EthereumNetwork { get; set; }
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