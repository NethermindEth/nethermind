using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using Nevermind.Evm;
using Nevermind.Store;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.VM.Test
{
    public class VMTestBase
    {
        private InMemoryDb _db;
        private IStorageProvider _storageProvider;
        private IBlockhashProvider _blockhashProvider;
        private IStateProvider _stateProvider;
        private readonly IProtocolSpecification _protocolSpecification = new OlympicProtocolSpecification();

        [SetUp]
        public void Setup()
        {
            _db = new InMemoryDb();
            _storageProvider = new StorageProvider(ShouldLog.State ? new ConsoleLogger() : null);
            _blockhashProvider = new TestBlockhashProvider();
            _stateProvider = new StateProvider(new StateTree(_db), _protocolSpecification, ShouldLog.State ? new ConsoleLogger() : null);
        }

        public static IEnumerable<VirtualMachineTest> LoadTests(string testSet)
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            IEnumerable<string> testDirs = Directory.EnumerateDirectories(".", "vm" + testSet);
            Dictionary<string, Dictionary<string, VirtualMachineTestJson>> testJsons =
                new Dictionary<string, Dictionary<string, VirtualMachineTestJson>>();
            foreach (string testDir in testDirs)
            {
                testJsons[testDir] = new Dictionary<string, VirtualMachineTestJson>();
                IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir).ToList();
                foreach (string testFile in testFiles)
                {
                    string json = File.ReadAllText(testFile);
                    Dictionary<string, VirtualMachineTestJson> testsInFile =
                        JsonConvert.DeserializeObject<Dictionary<string, VirtualMachineTestJson>>(json);
                    foreach (KeyValuePair<string, VirtualMachineTestJson> namedTest in testsInFile)
                    {
                        testJsons[testDir].Add(namedTest.Key, namedTest.Value);
                    }
                }
            }

            if (testJsons.Any())
            {
                return testJsons.First().Value.Select(pair => Convert(pair.Key, pair.Value));
            }

            return new VirtualMachineTest[0];
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
            environment.CurrentCoinbase = envJson.CurrentCoinbase == null ? null : new Address(envJson.CurrentCoinbase);
            environment.CurrentDifficulty = Hex.ToBytes(envJson.CurrentDifficulty).ToUnsignedBigInteger();
            environment.CurrentGasLimit = Hex.ToBytes(envJson.CurrentGasLimit).ToUnsignedBigInteger();
            environment.CurrentNumber = Hex.ToBytes(envJson.CurrentNumber).ToUnsignedBigInteger();
            environment.CurrentTimestamp = Hex.ToBytes(envJson.CurrentTimestamp).ToUnsignedBigInteger();
            return environment;
        }

        private static VirtualMachineTest Convert(string name, VirtualMachineTestJson testJson)
        {
            VirtualMachineTest test = new VirtualMachineTest();
            test.Name = name;
            test.Environment = Convert(testJson.Env);
            test.Execution = Convert(testJson.Exec);
            test.Gas = testJson.Gas == null ? (BigInteger?)null : Hex.ToBytes(testJson.Gas).ToUnsignedBigInteger();
            test.Logs = testJson.Logs == null ? null : Hex.ToBytes(testJson.Gas);
            test.Out = testJson.Out == null ? null : Hex.ToBytes(testJson.Out);
            test.Post = testJson.Post?.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            test.Pre = testJson.Pre.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            return test;
        }

        protected void RunTest(VirtualMachineTest test)
        {
            VirtualMachine machine = new VirtualMachine(_protocolSpecification, _stateProvider, _storageProvider, _blockhashProvider, ShouldLog.Evm ? new ConsoleLogger() : null);
            ExecutionEnvironment environment = new ExecutionEnvironment();
            environment.Value = test.Execution.Value;
            environment.CallDepth = 0;
            environment.Sender = test.Execution.Caller;
            environment.ExecutingAccount = test.Execution.Address;

            Block block = new Block(null, new BlockHeader[0], new Transaction[0]);
            BlockHeader header = new BlockHeader();
            header.Number = test.Environment.CurrentNumber;
            header.Difficulty = test.Environment.CurrentDifficulty;
            header.Timestamp = test.Environment.CurrentTimestamp;
            header.GasLimit = test.Environment.CurrentGasLimit;
            header.Beneficiary = test.Environment.CurrentCoinbase;
            block.Header = header;

            environment.CurrentBlock = header;

            environment.GasPrice = test.Execution.GasPrice;
            environment.InputData = test.Execution.Data;
            environment.MachineCode = test.Execution.Code;
            environment.Originator = test.Execution.Origin;

            foreach (KeyValuePair<Address, AccountState> accountState in test.Pre)
            {
                foreach (KeyValuePair<BigInteger, byte[]> storageItem in accountState.Value.Storage)
                {
                    _storageProvider.Set(accountState.Key, storageItem.Key, storageItem.Value);
                    if (accountState.Key.Equals(test.Execution.Address))
                    {
                        _storageProvider.Set(accountState.Key, storageItem.Key, storageItem.Value);
                    }
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

            EvmState state = new EvmState((ulong)test.Execution.Gas, environment, ExecutionType.Transaction, false);

            if (test.Out == null)
            {
                Assert.That(() => machine.Run(state), Throws.Exception);
                return;
            }

            _stateProvider.Commit();
            _storageProvider.Commit(_stateProvider);

            (byte[] output, TransactionSubstate substate) = machine.Run(state);

            Assert.True(Bytes.UnsafeCompare(test.Out, output),
                $"Exp: {Hex.FromBytes(test.Out, true)} != Actual: {Hex.FromBytes(output, true)}");
            Assert.AreEqual((ulong)test.Gas, state.GasAvailable);
            foreach (KeyValuePair<Address, AccountState> accountState in test.Post)
            {
                bool accountExists = _stateProvider.AccountExists(accountState.Key);
                BigInteger balance = accountExists ? _stateProvider.GetBalance(accountState.Key) : 0;
                BigInteger nonce = accountExists ? _stateProvider.GetNonce(accountState.Key) : 0;
                Assert.AreEqual(accountState.Value.Balance, balance, $"{accountState.Key} Balance");
                Assert.AreEqual(accountState.Value.Nonce, nonce, $"{accountState.Key} Nonce");

                // TODO: not testing properly 0 balance accounts
                if (accountExists)
                {
                    byte[] code = _stateProvider.GetCode(accountState.Key);
                    Assert.AreEqual(accountState.Value.Code, code, $"{accountState.Key} Code");
                }

                foreach (KeyValuePair<BigInteger, byte[]> storageItem in accountState.Value.Storage)
                {
                    byte[] value = _storageProvider.Get(accountState.Key, storageItem.Key);
                    Assert.True(Bytes.UnsafeCompare(storageItem.Value, value),
                        $"Storage[{accountState.Key}_{storageItem.Key}] Exp: {Hex.FromBytes(storageItem.Value, true)} != Actual: {Hex.FromBytes(value, true)}");
                }
            }
        }

        public class VirtualMachineTestJson
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
            public string CurrentCoinbase { get; set; }
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

        public class VirtualMachineTest
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