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

namespace Ethereum.GeneralState.Test
{
    public class GeneralTestBase
    {
        private IBlockhashProvider _blockhashProvider;
        private InMemoryDb _db;
        private IStateProvider _stateProvider;
        private IStorageProvider _storageProvider;
        private readonly IProtocolSpecification _protocolSpecification = new FrontierProtocolSpecification();
        private IVirtualMachine _virtualMachine;

        [SetUp]
        public void Setup()
        {
            _db = new InMemoryDb();
            _storageProvider = new StorageProvider();
            _blockhashProvider = new TestBlockhashProvider();
            _stateProvider = new StateProvider(new StateTree(_db), _protocolSpecification);
            _virtualMachine = new VirtualMachine(_blockhashProvider, _stateProvider, _storageProvider, _protocolSpecification, ShouldLog.Evm ? new ConsoleLogger() : null);
        }

        public static IEnumerable<GenerateStateTest> LoadTests(string testSet)
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            IEnumerable<string> testDirs = Directory.EnumerateDirectories(".\\Tests\\", "st" + testSet);
            Dictionary<string, Dictionary<string, GeneralStateTestJson>> testJsons =
                new Dictionary<string, Dictionary<string, GeneralStateTestJson>>();
            foreach (string testDir in testDirs)
            {
                testJsons[testDir] = new Dictionary<string, GeneralStateTestJson>();
                IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir).ToList();
                foreach (string testFile in testFiles)
                {
                    string json = File.ReadAllText(testFile);
                    Dictionary<string, GeneralStateTestJson> testsInFile =
                        JsonConvert.DeserializeObject<Dictionary<string, GeneralStateTestJson>>(json);
                    foreach (KeyValuePair<string, GeneralStateTestJson> namedTest in testsInFile)
                    {
                        testJsons[testDir].Add(namedTest.Key, namedTest.Value);
                    }
                }
            }

            if (testJsons.Any())
            {
                return testJsons.First().Value.Select(pair => Convert(pair.Key, pair.Value));
            }

            return new GenerateStateTest[0];
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

        private static IncomingTransaction Convert(TransactionJson transactionJson)
        {
            IncomingTransaction incomingTransaction = new IncomingTransaction();
            incomingTransaction.Data = Hex.ToBytes(transactionJson.Data[0]);
            incomingTransaction.Value = Hex.ToBytes(transactionJson.Value[0]).ToUnsignedBigInteger();
            incomingTransaction.GasLimit = Hex.ToBytes(transactionJson.GasLimit[0]).ToUnsignedBigInteger();
            incomingTransaction.GasPrice = Hex.ToBytes(transactionJson.GasPrice).ToUnsignedBigInteger();
            incomingTransaction.Nonce = Hex.ToBytes(transactionJson.Nonce).ToUnsignedBigInteger();
            incomingTransaction.To = string.IsNullOrWhiteSpace(transactionJson.To) ? null : new Address(new Hex(transactionJson.To));
            incomingTransaction.SecretKey = new PrivateKey(new Hex(transactionJson.SecretKey));
            return incomingTransaction;
        }

        private static GeneralState Convert(GeneralStateJson generalStateJson)
        {
            GeneralState generalState = new GeneralState();
            generalState.Hash = new Keccak(generalStateJson.Hash);
            generalState.Indexes = generalStateJson.Indexes;
            generalState.Logs = new Keccak(generalStateJson.Logs);
            return generalState;
        }

        private static Environment Convert(EnvironmentJson envJson)
        {
            Environment environment = new Environment();
            environment.CurrentCoinbase = envJson.CurrentCoinbase == null ? null : new Address(envJson.CurrentCoinbase);
            environment.CurrentDifficulty = Hex.ToBytes(envJson.CurrentDifficulty).ToUnsignedBigInteger();
            environment.CurrentGasLimit = Hex.ToBytes(envJson.CurrentGasLimit).ToUnsignedBigInteger();
            environment.CurrentNumber = Hex.ToBytes(envJson.CurrentNumber).ToUnsignedBigInteger();
            environment.CurrentTimestamp = Hex.ToBytes(envJson.CurrentTimestamp).ToUnsignedBigInteger();
            return environment;
        }

        private static GenerateStateTest Convert(string name, GeneralStateTestJson testJson)
        {
            GenerateStateTest test = new GenerateStateTest();
            test.Name = name;
            test.Environment = Convert(testJson.Env);
            test.IncomingTransaction = Convert(testJson.Transaction);
            test.Gas = testJson.Gas == null ? (BigInteger?)null : Hex.ToBytes(testJson.Gas).ToUnsignedBigInteger();
            test.Logs = testJson.Logs == null ? null : Hex.ToBytes(testJson.Gas);
            test.Out = testJson.Out == null ? null : Hex.ToBytes(testJson.Out);
            test.Post = testJson.Post?.ToDictionary(p => p.Key, p => p.Value.Select(Convert).ToArray());
            test.Pre = testJson.Pre.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            return test;
        }

        protected void RunTest(GenerateStateTest test)
        {
            foreach (KeyValuePair<Address, AccountState> accountState in test.Pre)
            {
                foreach (KeyValuePair<BigInteger, byte[]> storageItem in accountState.Value.Storage)
                {
                    _storageProvider.Set(accountState.Key, storageItem.Key, storageItem.Value);
                }

                _stateProvider.UpdateCode(accountState.Value.Code);

                _stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance);
                _stateProvider.UpdateStorageRoot(accountState.Key, _storageProvider.GetRoot(accountState.Key));
                Keccak codeHash = _stateProvider.UpdateCode(accountState.Value.Code);
                _stateProvider.UpdateCodeHash(accountState.Key, codeHash);
                for (int i = 0; i < accountState.Value.Nonce; i++)
                {
                    _stateProvider.IncrementNonce(accountState.Key);
                }
            }

            TransactionProcessor processor =
                new TransactionProcessor(_virtualMachine, _stateProvider, _storageProvider, _protocolSpecification, ChainId.Mainnet, ShouldLog.TransactionProcessor ? new ConsoleLogger() : null);
            Transaction transaction = new Transaction();
            transaction.To = test.IncomingTransaction.To;
            transaction.Value = test.IncomingTransaction.Value;
            transaction.GasLimit = test.IncomingTransaction.GasLimit;
            transaction.GasPrice = test.IncomingTransaction.GasPrice;
            transaction.Data = test.IncomingTransaction.Data;
            transaction.Init = null; // code here / create
            transaction.Nonce = test.IncomingTransaction.Nonce;

            Signer.Sign(transaction, test.IncomingTransaction.SecretKey, false, 0); // check EIP and chain id

            Block block = new Block(null, new BlockHeader[0], new Transaction[0]);
            BlockHeader header = new BlockHeader();
            header.Number = test.Environment.CurrentNumber;
            header.Difficulty = test.Environment.CurrentDifficulty;
            header.Timestamp = test.Environment.CurrentTimestamp;
            header.GasLimit = test.Environment.CurrentGasLimit;
            header.Beneficiary = test.Environment.CurrentCoinbase;
            block.Header = header;

            Address sender = test.IncomingTransaction.SecretKey.Address;
            TransactionReceipt receipt = processor.Execute(
                transaction,
                header,
                BigInteger.Zero
            );

            Assert.AreEqual(test.Post["Frontier"][0].Hash, receipt.PostTransactionState);
        }

        public class GeneralStateTestJson
        {
            public EnvironmentJson Env { get; set; }
            public TransactionJson Transaction { get; set; }
            public string Gas { get; set; }
            public string Logs { get; set; }
            public string Out { get; set; }
            public Dictionary<string, AccountStateJson> Pre { get; set; }
            public Dictionary<string, GeneralStateJson[]> Post { get; set; }
        }

        public class AccountState
        {
            public BigInteger Balance { get; set; }
            public byte[] Code { get; set; }
            public BigInteger Nonce { get; set; }
            public Dictionary<BigInteger, byte[]> Storage { get; set; }
        }

        public class GeneralStateIndexes
        {
            public string Data { get; set; }
            public string Gas { get; set; }
            public string Value { get; set; }
        }

        public class GeneralState
        {
            public Keccak Hash { get; set; }
            public GeneralStateIndexes Indexes { get; set; }
            public Keccak Logs { get; set; }
        }

        public class GeneralStateJson
        {
            public string Hash { get; set; }
            public GeneralStateIndexes Indexes { get; set; }
            public string Logs { get; set; }
        }

        public class AccountStateJson
        {
            public string Balance { get; set; }
            public string Code { get; set; }
            public string Nonce { get; set; }
            public Dictionary<string, string> Storage { get; set; }
        }

        public class Environment
        {
            public Address CurrentCoinbase { get; set; }
            public BigInteger CurrentDifficulty { get; set; }
            public BigInteger CurrentGasLimit { get; set; }
            public BigInteger CurrentNumber { get; set; }
            public BigInteger CurrentTimestamp { get; set; }
        }

        public class EnvironmentJson
        {
            public string CurrentCoinbase { get; set; }
            public string CurrentDifficulty { get; set; }
            public string CurrentGasLimit { get; set; }
            public string CurrentNumber { get; set; }
            public string CurrentTimestamp { get; set; }
        }

        public class IncomingTransaction
        {
            public byte[] Data { get; set; }
            public BigInteger GasLimit { get; set; }
            public BigInteger GasPrice { get; set; }
            public BigInteger Nonce { get; set; }
            public PrivateKey SecretKey { get; set; }
            public Address To { get; set; }
            public BigInteger Value { get; set; }
        }

        public class TransactionJson
        {
            public string[] Data { get; set; }
            public string[] GasLimit { get; set; }
            public string GasPrice { get; set; }
            public string Nonce { get; set; }
            public string SecretKey { get; set; }
            public string To { get; set; }
            public string[] Value { get; set; }
        }

        public class GenerateStateTest
        {
            public string Name { get; set; }
            public Environment Environment { get; set; }
            public IncomingTransaction IncomingTransaction { get; set; }
            public BigInteger? Gas { get; set; }
            public byte[] Logs { get; set; }
            public byte[] Out { get; set; }
            public Dictionary<Address, AccountState> Pre { get; set; }
            public Dictionary<string, GeneralState[]> Post { get; set; }

            public override string ToString()
            {
                return Name;
            }
        }
    }
}