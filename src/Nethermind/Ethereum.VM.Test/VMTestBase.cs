/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Store;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.VM.Test
{
    public class VMTestBase
    {
        private IDbProvider _dbProvider;
        private IStorageProvider _storageProvider;
        private IBlockhashProvider _blockhashProvider;
        private IStateProvider _stateProvider;
        private ILogManager _logManager = NullLogManager.Instance;
        private readonly IReleaseSpec _releaseSpec = Olympic.Instance;

        [SetUp]
        public void Setup()
        {
            _dbProvider = new MemDbProvider(_logManager);
            _blockhashProvider = new TestBlockhashProvider();
            _stateProvider = new StateProvider(new StateTree(_dbProvider.GetOrCreateStateDb()), _dbProvider.GetOrCreateCodeDb(), _logManager);
            _storageProvider = new StorageProvider(new MemDbProvider(_logManager), _stateProvider, _logManager);
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
            state.Balance = Bytes.FromHexString(accountStateJson.Balance).ToUnsignedBigInteger();
            state.Code = Bytes.FromHexString(accountStateJson.Code);
            state.Nonce = Bytes.FromHexString(accountStateJson.Nonce).ToUnsignedBigInteger();
            state.Storage = accountStateJson.Storage.ToDictionary(
                p => Bytes.FromHexString(p.Key).ToUnsignedBigInteger(),
                p => Bytes.FromHexString(p.Value));
            return state;
        }

        private static Execution Convert(ExecJson execJson)
        {
            Execution environment = new Execution();
            environment.Address = execJson.Address == null ? null : new Address(execJson.Address);
            environment.Caller = execJson.Caller == null ? null : new Address(execJson.Caller);
            environment.Origin = execJson.Origin == null ? null : new Address(execJson.Origin);
            environment.Code = Bytes.FromHexString(execJson.Code);
            environment.Data = Bytes.FromHexString(execJson.Data);
            environment.Gas = Bytes.FromHexString(execJson.Gas).ToUnsignedBigInteger();
            environment.GasPrice = Bytes.FromHexString(execJson.GasPrice).ToUnsignedBigInteger();
            environment.Value = Bytes.FromHexString(execJson.Value).ToUnsignedBigInteger();
            return environment;
        }

        private static Environment Convert(EnvironmentJson envJson)
        {
            Environment environment = new Environment();
            environment.CurrentCoinbase = envJson.CurrentCoinbase == null ? null : new Address(envJson.CurrentCoinbase);
            environment.CurrentDifficulty = Bytes.FromHexString(envJson.CurrentDifficulty).ToUnsignedBigInteger();
            environment.CurrentGasLimit = Bytes.FromHexString(envJson.CurrentGasLimit).ToUnsignedBigInteger();
            environment.CurrentNumber = Bytes.FromHexString(envJson.CurrentNumber).ToUnsignedBigInteger();
            environment.CurrentTimestamp = Bytes.FromHexString(envJson.CurrentTimestamp).ToUnsignedBigInteger();
            return environment;
        }

        private static VirtualMachineTest Convert(string name, VirtualMachineTestJson testJson)
        {
            VirtualMachineTest test = new VirtualMachineTest();
            test.Name = name;
            test.Environment = Convert(testJson.Env);
            test.Execution = Convert(testJson.Exec);
            test.Gas = testJson.Gas == null ? (BigInteger?)null : Bytes.FromHexString(testJson.Gas).ToUnsignedBigInteger();
            test.Logs = testJson.Logs == null ? null : Bytes.FromHexString(testJson.Gas);
            test.Out = testJson.Out == null ? null : Bytes.FromHexString(testJson.Out);
            test.Post = testJson.Post?.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            test.Pre = testJson.Pre.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            return test;
        }

        protected void RunTest(VirtualMachineTest test)
        {
            VirtualMachine machine = new VirtualMachine(_stateProvider, _storageProvider, _blockhashProvider, _logManager);
            ExecutionEnvironment environment = new ExecutionEnvironment();
            environment.Value = test.Execution.Value;
            environment.CallDepth = 0;
            environment.Sender = test.Execution.Caller;
            environment.ExecutingAccount = test.Execution.Address;

            
            BlockHeader header = new BlockHeader(
                Keccak.OfAnEmptyString,
                Keccak.OfAnEmptySequenceRlp,
                test.Environment.CurrentCoinbase,
                test.Environment.CurrentDifficulty,
                test.Environment.CurrentNumber,
                (long)test.Environment.CurrentGasLimit,
                test.Environment.CurrentTimestamp, Bytes.Empty);
            
            environment.CurrentBlock = header;

            environment.GasPrice = test.Execution.GasPrice;
            environment.InputData = test.Execution.Data;
            environment.CodeInfo = new CodeInfo(test.Execution.Code);
            environment.Originator = test.Execution.Origin;

            foreach (KeyValuePair<Address, AccountState> accountState in test.Pre)
            {
                foreach (KeyValuePair<BigInteger, byte[]> storageItem in accountState.Value.Storage)
                {
                    _storageProvider.Set(new StorageAddress(accountState.Key, storageItem.Key), storageItem.Value);
                    if (accountState.Key.Equals(test.Execution.Address))
                    {
                        _storageProvider.Set(new StorageAddress(accountState.Key, storageItem.Key), storageItem.Value);
                    }
                }

                _stateProvider.UpdateCode(accountState.Value.Code);

                _stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance);
                _stateProvider.UpdateStorageRoot(accountState.Key, _storageProvider.GetRoot(accountState.Key));
                Keccak codeHash = _stateProvider.UpdateCode(accountState.Value.Code);
                _stateProvider.UpdateCodeHash(accountState.Key, codeHash, Olympic.Instance);
                for (int i = 0; i < accountState.Value.Nonce; i++)
                {
                    _stateProvider.IncrementNonce(accountState.Key);
                }
            }

            EvmState state = new EvmState((long)test.Execution.Gas, environment, ExecutionType.Transaction, false);

            if (test.Out == null)
            {
                Assert.That(() => machine.Run(state, Olympic.Instance, null), Throws.Exception);
                return;
            }

            _stateProvider.Commit(Olympic.Instance);
            _storageProvider.Commit(Olympic.Instance);

            (byte[] output, TransactionSubstate substate) = machine.Run(state, Olympic.Instance, null);

            Assert.True(Bytes.UnsafeCompare(test.Out, output),
                $"Exp: {test.Out.ToHexString(true)} != Actual: {output.ToHexString(true)}");
            Assert.AreEqual((long)test.Gas, state.GasAvailable);
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
                    byte[] value = _storageProvider.Get(new StorageAddress(accountState.Key, storageItem.Key));
                    Assert.True(Bytes.UnsafeCompare(storageItem.Value, value),
                        $"Storage[{accountState.Key}_{storageItem.Key}] Exp: {storageItem.Value.ToHexString(true)} != Actual: {value.ToHexString(true)}");
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