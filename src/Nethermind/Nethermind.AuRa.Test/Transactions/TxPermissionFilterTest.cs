//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.AuRa.Test.Contract;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class TxPermissionFilterTest
    {
        private const string ContractAddress = "0xAB5b100cf7C8deFB3c8f3C48474223997A50fB13";
        private static Address _contractAddress = new Address(ContractAddress);
        
        private static readonly TransactionPermissionContract.TxPermissions[] TxTypes = new[]
        {
            TransactionPermissionContract.TxPermissions.Basic,
            TransactionPermissionContract.TxPermissions.Create,
            TransactionPermissionContract.TxPermissions.Call,
        };

        public static IEnumerable<TestCaseData> V1Tests()
        {
            IList<Test> tests = new List<Test>()
            {
                new Test() {SenderKey = GetPrivateKey(1), ContractPermissions = TransactionPermissionContract.TxPermissions.All},
                new Test() {SenderKey = GetPrivateKey(2), ContractPermissions = TransactionPermissionContract.TxPermissions.Basic | TransactionPermissionContract.TxPermissions.Call},
                new Test() {SenderKey = GetPrivateKey(3), ContractPermissions = TransactionPermissionContract.TxPermissions.Basic, To = _contractAddress},
                new Test() {SenderKey = GetPrivateKey(4), ContractPermissions = TransactionPermissionContract.TxPermissions.None},
            };

            return GetTestCases(tests, nameof(V1), CreateV1Transaction);
        }
        
        private static TransactionBuilder<Transaction> CreateV1Transaction(Test test, TransactionPermissionContract.TxPermissions txType)
        {
            var transactionBuilder = Build.A.Transaction.WithData(null).WithSenderAddress(test.Sender);
            
            switch (txType)
            {
                case TransactionPermissionContract.TxPermissions.Call:
                    transactionBuilder.WithData(Bytes.Zero32);
                    transactionBuilder.To(test.To);
                    break;
                case TransactionPermissionContract.TxPermissions.Create:
                    transactionBuilder.WithInit(Bytes.Zero32);
                    break;
            }
            
            return transactionBuilder;
        }

        // Contract code: https://gist.github.com/arkpar/38a87cb50165b7e683585eec71acb05a
        [TestCaseSource(nameof(V1Tests))]
        public async Task<(bool IsAllowed, bool Cache)> V1(Func<Task<TestTxPermissionsBlockchain>> chainFactory, Transaction tx) => await ChainTest(chainFactory, tx, 1);

        public static IEnumerable<TestCaseData> V2Tests()
        {
            IList<Test> tests = new List<Test>()
            {
                new Test() {SenderKey = GetPrivateKey(1), ContractPermissions = TransactionPermissionContract.TxPermissions.All, Cache = true},
                new Test() {SenderKey = GetPrivateKey(2), ContractPermissions = TransactionPermissionContract.TxPermissions.Basic | TransactionPermissionContract.TxPermissions.Call, Cache = true},
                new Test() {SenderKey = GetPrivateKey(3), ContractPermissions = TransactionPermissionContract.TxPermissions.Basic, Cache = true, To = _contractAddress},
                new Test() {SenderKey = GetPrivateKey(4), ContractPermissions = TransactionPermissionContract.TxPermissions.None, Cache = true},
                
                new Test() {SenderKey = GetPrivateKey(5), ContractPermissions = TransactionPermissionContract.TxPermissions.None, Cache = true},
                new Test() {SenderKey = GetPrivateKey(5), ContractPermissions = TransactionPermissionContract.TxPermissions.All, Cache = false, Value = 0},
                
                new Test() {SenderKey = GetPrivateKey(6), ContractPermissions = TransactionPermissionContract.TxPermissions.None, Cache = true},
                new Test() {SenderKey = GetPrivateKey(6), ContractPermissions = TransactionPermissionContract.TxPermissions.Basic, Cache = false, ToKey = GetPrivateKey(7)},
                
                new Test() {SenderKey = GetPrivateKey(7), ContractPermissions = TransactionPermissionContract.TxPermissions.None, Cache = true},
                new Test() {SenderKey = GetPrivateKey(7), ContractPermissions = TransactionPermissionContract.TxPermissions.None, Cache = true, Value = 0},
                new Test() {SenderKey = GetPrivateKey(7), ContractPermissions = TransactionPermissionContract.TxPermissions.None, Cache = true, ToKey = GetPrivateKey(6)},
                new Test() {SenderKey = GetPrivateKey(7), ContractPermissions = TransactionPermissionContract.TxPermissions.All, Cache = false, ToKey = GetPrivateKey(6), Value = 0},
            };

            return GetTestCases(tests, nameof(V2), CreateV2Transaction);
        }
        
        private static TransactionBuilder<Transaction> CreateV2Transaction(Test test, TransactionPermissionContract.TxPermissions txType)
        {
            var transactionBuilder = CreateV1Transaction(test, txType);
            transactionBuilder.To(test.To);
            
            switch (txType)
            {
                case TransactionPermissionContract.TxPermissions.Basic:
                {
                    if (test.To == _contractAddress)
                    {
                        transactionBuilder.To(Address.Zero);
                    }

                    break;
                }
                case TransactionPermissionContract.TxPermissions.Call:
                    if (test.Number == 6 && test.To == GetPrivateKey(7).Address)
                    {
                        transactionBuilder.To(_contractAddress);
                        test.Cache = true;
                    }

                    break;
            }

            transactionBuilder.WithValue(test.Value);
            return transactionBuilder;
        }
        
        // Contract code: https://gist.github.com/VladLupashevskyi/84f18eabb1e4afadf572cf92af3e7e7f
        [TestCaseSource(nameof(V2Tests))]
        public async Task<(bool IsAllowed, bool Cache)> V2(Func<Task<TestTxPermissionsBlockchain>> chainFactory, Transaction tx) => await ChainTest(chainFactory, tx, 2);
        
        public static IEnumerable<TestCaseData> V3Tests()
        {
            IList<Test> tests = new List<Test>()
            {
                new Test() {SenderKey = GetPrivateKey(1), ContractPermissions = TransactionPermissionContract.TxPermissions.None, Cache = false},
                new Test() {SenderKey = GetPrivateKey(1), ContractPermissions = TransactionPermissionContract.TxPermissions.All, Cache = false, GasPrice = 1},
                new Test() {SenderKey = GetPrivateKey(1), ContractPermissions = TransactionPermissionContract.TxPermissions.All, Cache = false, Data = new byte[]{0, 1}},
                new Test() {SenderKey = GetPrivateKey(1), ContractPermissions = TransactionPermissionContract.TxPermissions.All, Cache = false, GasPrice = 5, Data = new byte[]{0, 2, 3}},
            };

            return GetTestCases(tests, nameof(V3), CreateV3Transaction);
        }
        
        private static TransactionBuilder<Transaction> CreateV3Transaction(Test test, TransactionPermissionContract.TxPermissions txType)
        {
            var transactionBuilder = CreateV2Transaction(test, txType);
            transactionBuilder.WithData(test.Data);
            transactionBuilder.WithGasPrice(test.GasPrice);
            return transactionBuilder;
        }
        
        [TestCaseSource(nameof(V3Tests))]
        public async Task<(bool IsAllowed, bool Cache)> V3(Func<Task<TestTxPermissionsBlockchain>> chainFactory, Transaction tx) => await ChainTest(chainFactory, tx, 3);

        private static async Task<(bool IsAllowed, bool Cache)> ChainTest(Func<Task<TestTxPermissionsBlockchain>> chainFactory, Transaction tx, UInt256 version)
        {
            var chain = await chainFactory();
            var head = chain.BlockTree.Head;
            var isAllowed = chain.TxPermissionFilter.IsAllowed(tx, head.Header);
            chain.TxPermissionFilterCache.VersionedContracts.Get(head.Hash).Should().Be(version);
            return (isAllowed, chain.TxPermissionFilterCache.Permissions.Contains((head.Hash, tx.SenderAddress)));
        }

        private static IEnumerable<TestCaseData> GetTestCases(IEnumerable<Test> tests, string testsName, Func<Test, TransactionPermissionContract.TxPermissions, TransactionBuilder<Transaction>> transactionBuilder)
        {
            TestCaseData GetTestCase(
                Func<Task<TestTxPermissionsBlockchain>> chainFactory,
                Test test,
                TransactionPermissionContract.TxPermissions txType)
            {
                var result = (test.ContractPermissions & txType) != TransactionPermissionContract.TxPermissions.None;
                return new TestCaseData(chainFactory, transactionBuilder(test, txType).TestObject)
                    .SetName($"{testsName} - {test.Number}: Expected {test.ContractPermissions}, check {txType} is {result}")
                    .SetCategory(testsName + "Tests")
                    .Returns((result, test.Cache ?? true));
            }

            var chainTask = TestContractBlockchain.ForTest<TestTxPermissionsBlockchain, TxPermissionFilterTest>(testsName);
            Func<Task<TestTxPermissionsBlockchain>> testFactory = async () =>
            {
                var chain = await chainTask;
                chain.TxPermissionFilterCache.Permissions.Clear();
                chain.TxPermissionFilterCache.VersionedContracts.Clear();
                return chain;
            };

            foreach (var test in tests)
            {
                foreach (var txType in TxTypes)
                {
                    yield return GetTestCase(testFactory, test, txType);
                }
            }
        }

        private static PrivateKey GetPrivateKey(int key) => new PrivateKey(key.ToString("X64"));

        [TestCase(1, ExpectedResult = true)]
        [TestCase(3, ExpectedResult = true)]
        [TestCase(4, ExpectedResult = false)]
        [TestCase(5, ExpectedResult = false)]
        [TestCase(10, ExpectedResult = false)]
        public bool allows_transactions_before_transitions(long blockNumber)
        {
            var transactionPermissionContract = new TransactionPermissionContract(
                Substitute.For<ITransactionProcessor>(), 
                Substitute.For<IAbiEncoder>(),
                TestItem.AddressA,
                5, 
                Substitute.For<IReadOnlyTransactionProcessorSource>());
            
            var filter = new TxPermissionFilter(transactionPermissionContract, new ITxPermissionFilter.Cache(), Substitute.For<IStateProvider>(), LimboLogs.Instance);
            return filter.IsAllowed(Build.A.Transaction.TestObject, Build.A.BlockHeader.WithNumber(blockNumber).TestObject);
        }

        public class TestTxPermissionsBlockchain : TestContractBlockchain
        {
            public TxPermissionFilter TxPermissionFilter { get; private set; }
            public ITxPermissionFilter.Cache TxPermissionFilterCache { get; private set; }
            
            protected override BlockProcessor CreateBlockProcessor()
            {
                var validator = new AuRaParameters.Validator()
                {
                    Addresses = TestItem.Addresses,
                    ValidatorType = AuRaParameters.ValidatorType.List
                };

                var transactionPermissionContract = new TransactionPermissionContract(TxProcessor, new AbiEncoder(), _contractAddress, 1,
                    new ReadOnlyTransactionProcessorSource(DbProvider, BlockTree, SpecProvider, LimboLogs.Instance));

                TxPermissionFilterCache = new ITxPermissionFilter.Cache();
                TxPermissionFilter = new TxPermissionFilter(transactionPermissionContract, TxPermissionFilterCache, State, LimboLogs.Instance);
                
                return new AuRaBlockProcessor(SpecProvider, Always.Valid, new RewardCalculator(SpecProvider), TxProcessor, StateDb, CodeDb, State, Storage, TxPool, ReceiptStorage, LimboLogs.Instance,
                    new ListBasedValidator(validator, new ValidSealerStrategy(), LimboLogs.Instance), BlockTree, TxPermissionFilter);
            }

            protected override Task AddBlocksOnStart() => Task.CompletedTask;
        }

        public class Test
        {
            private Address _to;
            public PrivateKey SenderKey { get; set; }
            public PrivateKey ToKey { get; set; }
            public UInt256 Value { get; set; } = 1;
            public byte[] Data { get; set; } = Bytes.Zero32;
            public UInt256 GasPrice { get; set; } = 0;
            public Address Sender => SenderKey.Address;
            public Address To
            {
                get => _to ?? ToKey?.Address ?? Address.Zero;
                set => _to = value;
            }

            public TransactionPermissionContract.TxPermissions ContractPermissions { get; set; }
            public bool? Cache { get; set; }
            public int Number => int.Parse(SenderKey.KeyBytes.ToHexString(), NumberStyles.HexNumber);
        }
    }
}