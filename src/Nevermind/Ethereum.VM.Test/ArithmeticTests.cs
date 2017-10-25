using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using Nevermind.Evm;
using Nevermind.Store;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.VM.Test
{
    public class TestStorageProvider : IStorageProvider
    {
        private readonly InMemoryDb _db;

        private readonly Dictionary<Address, StorageTree> _storages = new Dictionary<Address, StorageTree>();

        public TestStorageProvider(InMemoryDb db)
        {
            _db = db;
        }

        public StorageTree GetStorage(Address address)
        {
            return _storages[address];
        }

        public StorageTree GetOrCreateStorage(Address address)
        {
            if (!_storages.ContainsKey(address))
            {
                _storages[address] = new StorageTree(_db);
            }

            return GetStorage(address);
        }
    }

    [TestFixture]
    public class ArithmeticTests
    {
        [SetUp]
        public void Setup()
        {
            _db = new InMemoryDb();
            _storageProvider = new TestStorageProvider(_db);
        }

        private InMemoryDb _db;
        private IStorageProvider _storageProvider;

        public static IEnumerable<ArithmeticTest> LoadTests(string testSet)
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            IEnumerable<string> testDirs = Directory.EnumerateDirectories(".", "vm" + testSet);
            Dictionary<string, Dictionary<string, ArithmeticTestJson>> testJsons =
                new Dictionary<string, Dictionary<string, ArithmeticTestJson>>();
            foreach (string testDir in testDirs)
            {
                testJsons[testDir] = new Dictionary<string, ArithmeticTestJson>();
                IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir).ToList();
                foreach (string testFile in testFiles)
                {
                    string json = File.ReadAllText(testFile);
                    Dictionary<string, ArithmeticTestJson> testsInFile =
                        JsonConvert.DeserializeObject<Dictionary<string, ArithmeticTestJson>>(json);
                    foreach (KeyValuePair<string, ArithmeticTestJson> namedTest in testsInFile)
                    {
                        testJsons[testDir].Add(namedTest.Key, namedTest.Value);
                    }
                }
            }

            return testJsons.First().Value.Select(pair => Convert(pair.Key, pair.Value));
        }

        private static AccountState Convert(AccountStateJson accountStateJson)
        {
            AccountState state = new AccountState();
            state.Balance = Hex.ToBytes(accountStateJson.Balance).ToUnsignedBigInteger();
            state.Code = Hex.ToBytes(accountStateJson.Balance);
            state.Nonce = Hex.ToBytes(accountStateJson.Balance).ToUnsignedBigInteger();
            state.Storage = accountStateJson.Storage.ToDictionary(
                p => Hex.ToBytes(p.Key).ToUnsignedBigInteger(),
                p => Hex.ToBytes(p.Value));
            return state;
        }

        private static Execution Convert(ExecJson execJson)
        {
            Execution environment = new Execution();
            environment.Address = execJson.Address == null ? null : new Address(execJson.Address);
            environment.Caller = execJson.Caller == null ? null : new Address(execJson.Caller);
            environment.Origin = execJson.Origin == null ? null : new Address(execJson.Origin);
            environment.Code = Hex.ToBytes(execJson.Code);
            environment.Data = Hex.ToBytes(execJson.Data);
            environment.Gas = Hex.ToBytes(execJson.Gas).ToUnsignedBigInteger();
            environment.GasPrice = Hex.ToBytes(execJson.GasPrice).ToUnsignedBigInteger();
            environment.Value = Hex.ToBytes(execJson.Value).ToUnsignedBigInteger();
            return environment;
        }

        private static Environment Convert(EnvironmentJson envJson)
        {
            Environment environment = new Environment();
            environment.CurrentCoinbase = envJson.Coinbase == null ? null : new Address(envJson.Coinbase);
            environment.CurrentDifficulty = Hex.ToBytes(envJson.CurrentDifficulty).ToUnsignedBigInteger();
            environment.CurrentGasLimit = Hex.ToBytes(envJson.CurrentGasLimit).ToUnsignedBigInteger();
            environment.CurrentNumber = Hex.ToBytes(envJson.CurrentNumber).ToUnsignedBigInteger();
            environment.CurrentTimestamp = Hex.ToBytes(envJson.CurrentTimestamp).ToUnsignedBigInteger();
            return environment;
        }

        private static ArithmeticTest Convert(string name, ArithmeticTestJson testJson)
        {
            ArithmeticTest test = new ArithmeticTest();
            test.Name = name;
            test.Environment = Convert(testJson.Env);
            test.Execution = Convert(testJson.Exec);
            test.Gas = testJson.Gas == null ? (BigInteger?) null : Hex.ToBytes(testJson.Gas).ToUnsignedBigInteger();
            test.Logs = testJson.Logs == null ? null : Hex.ToBytes(testJson.Gas);
            test.Out = testJson.Out == null ? null : Hex.ToBytes(testJson.Out);
            test.Post = testJson.Post?.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            test.Pre = testJson.Pre.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            return test;
        }

        [TestCaseSource(nameof(LoadTests), new object[] {"ArithmeticTest"})]
        public void Test(ArithmeticTest test)
        {
            VirtualMachine machine = new VirtualMachine();
            ExecutionEnvironment environment = new ExecutionEnvironment();
            environment.Value = test.Execution.Value;
            environment.CallDepth = 0;
            environment.Caller = test.Execution.Caller;
            environment.CodeOwner = test.Execution.Address;

            //environment.CurrentBlock = test.Environment.CurrentNumber

            environment.GasPrice = test.Execution.GasPrice;
            environment.InputData = test.Execution.Data;
            environment.MachineCode = test.Execution.Code;
            environment.Originator = test.Execution.Origin;

            MachineState state = new MachineState(test.Execution.Gas);
            foreach (KeyValuePair<Address, AccountState> accountState in test.Pre)
            {
                StorageTree storageTree = _storageProvider.GetOrCreateStorage(accountState.Key);
                foreach (KeyValuePair<BigInteger, byte[]> storageItem in accountState.Value.Storage)
                {
                    storageTree.Set(storageItem.Key, storageItem.Value);
                }
            }

            if (test.Out == null)
            {
                Assert.That(() =>  machine.Run(environment, state, _storageProvider), Throws.Exception);
                return;
            }

            byte[] result = machine.Run(environment, state, _storageProvider);

            Assert.True(Bytes.UnsafeCompare(test.Out, result));
            Assert.AreEqual(test.Gas, state.GasAvailable);
            foreach (KeyValuePair<Address, AccountState> accountState in test.Post)
            {
                StorageTree storage = _storageProvider.GetStorage(accountState.Key);
                foreach (KeyValuePair<BigInteger, byte[]> storageItem in accountState.Value.Storage)
                {
                    byte[] value = storage.Get(storageItem.Key);
                    Assert.True(Bytes.UnsafeCompare(storageItem.Value, value), $"Exp: {Hex.FromBytes(storageItem.Value, true)} != Actual: {Hex.FromBytes(value, true)}");
                }
            }
        }

        public class ArithmeticTestJson
        {
            public EnvironmentJson Env { get; set; }
            public ExecJson Exec { get; set; }
            public string Gas { get; set; }
            public string Logs { get; set; }
            public string Out { get; set; }
            public Dictionary<string, AccountStateJson> Pre { get; set; }
            public Dictionary<string, AccountStateJson> Post { get; set; }
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
            public string Coinbase { get; set; }
            public string CurrentDifficulty { get; set; }
            public string CurrentGasLimit { get; set; }
            public string CurrentNumber { get; set; }
            public string CurrentTimestamp { get; set; }
        }

        public class Execution
        {
            public Address Address { get; set; }
            public Address Caller { get; set; }
            public byte[] Code { get; set; }
            public byte[] Data { get; set; }
            public BigInteger Gas { get; set; }
            public BigInteger GasPrice { get; set; }
            public Address Origin { get; set; }
            public BigInteger Value { get; set; }
        }

        public class ExecJson
        {
            public string Address { get; set; }
            public string Caller { get; set; }
            public string Code { get; set; }
            public string Data { get; set; }
            public string Gas { get; set; }
            public string GasPrice { get; set; }
            public string Origin { get; set; }
            public string Value { get; set; }
        }

        public class ArithmeticTest
        {
            public string Name { get; set; }
            public Environment Environment { get; set; }
            public Execution Execution { get; set; }
            public BigInteger? Gas { get; set; }
            public byte[] Logs { get; set; }
            public byte[] Out { get; set; }
            public Dictionary<Address, AccountState> Pre { get; set; }
            public Dictionary<Address, AccountState> Post { get; set; }

            public override string ToString()
            {
                return Name;
            }
        }
    }
}