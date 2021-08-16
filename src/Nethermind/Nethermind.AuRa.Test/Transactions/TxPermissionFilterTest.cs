//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class TxPermissionFilterTest
    {
        private const string ContractAddress = "0xAB5b100cf7C8deFB3c8f3C48474223997A50fB13";
        private static readonly Address _contractAddress = new(ContractAddress);
        
        private static readonly ITransactionPermissionContract.TxPermissions[] TxPermissionsTypes = new[]
        {
            ITransactionPermissionContract.TxPermissions.Basic,
            ITransactionPermissionContract.TxPermissions.Call,
            ITransactionPermissionContract.TxPermissions.Create,
        };

        public static IEnumerable<TestCaseData> V1Tests()
        {
            IList<Test> tests = new List<Test>()
            {
                new() {SenderKey = GetPrivateKey(1), ContractPermissions = ITransactionPermissionContract.TxPermissions.All},
                new() {SenderKey = GetPrivateKey(2), ContractPermissions = ITransactionPermissionContract.TxPermissions.Basic | ITransactionPermissionContract.TxPermissions.Call},
                new() {SenderKey = GetPrivateKey(3), ContractPermissions = ITransactionPermissionContract.TxPermissions.Basic, To = _contractAddress},
                new() {SenderKey = GetPrivateKey(4), ContractPermissions = ITransactionPermissionContract.TxPermissions.None},
            };

            return GetTestCases(tests, nameof(V1), CreateV1Transaction);
        }
        
        private static TransactionBuilder<Transaction> CreateV1Transaction(Test test, ITransactionPermissionContract.TxPermissions txType)
        {
            var transactionBuilder = Build.A.Transaction.WithData(null).WithSenderAddress(test.Sender);
            
            switch (txType)
            {
                case ITransactionPermissionContract.TxPermissions.Call:
                    transactionBuilder.WithData(Bytes.Zero32);
                    transactionBuilder.To(test.To);
                    break;
                case ITransactionPermissionContract.TxPermissions.Create:
                    transactionBuilder.WithCode(Bytes.Zero32);
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
                new() {SenderKey = GetPrivateKey(1), ContractPermissions = ITransactionPermissionContract.TxPermissions.All, Cache = true},
                new() {SenderKey = GetPrivateKey(2), ContractPermissions = ITransactionPermissionContract.TxPermissions.Basic | ITransactionPermissionContract.TxPermissions.Call, Cache = true},
                new() {SenderKey = GetPrivateKey(3), ContractPermissions = ITransactionPermissionContract.TxPermissions.Basic, Cache = true, To = _contractAddress},
                new() {SenderKey = GetPrivateKey(4), ContractPermissions = ITransactionPermissionContract.TxPermissions.None, Cache = true},
                
                new() {SenderKey = GetPrivateKey(5), ContractPermissions = ITransactionPermissionContract.TxPermissions.None, Cache = true},
                new() {SenderKey = GetPrivateKey(5), ContractPermissions = ITransactionPermissionContract.TxPermissions.All, Cache = false, Value = 0},
                
                new() {SenderKey = GetPrivateKey(6), ContractPermissions = ITransactionPermissionContract.TxPermissions.None, Cache = true},
                new() {SenderKey = GetPrivateKey(6), ContractPermissions = ITransactionPermissionContract.TxPermissions.Basic, Cache = false, ToKey = GetPrivateKey(7)},
                
                new() {SenderKey = GetPrivateKey(7), ContractPermissions = ITransactionPermissionContract.TxPermissions.None, Cache = true},
                new() {SenderKey = GetPrivateKey(7), ContractPermissions = ITransactionPermissionContract.TxPermissions.None, Cache = true, Value = 0},
                new() {SenderKey = GetPrivateKey(7), ContractPermissions = ITransactionPermissionContract.TxPermissions.None, Cache = true, ToKey = GetPrivateKey(6)},
                new() {SenderKey = GetPrivateKey(7), ContractPermissions = ITransactionPermissionContract.TxPermissions.Basic | ITransactionPermissionContract.TxPermissions.Call, Cache = false, ToKey = GetPrivateKey(6), Value = 0},
            };

            return GetTestCases(tests, nameof(V2), CreateV2Transaction);
        }
        
        private static TransactionBuilder<Transaction> CreateV2Transaction(Test test, ITransactionPermissionContract.TxPermissions txPermissions)
        {
            var transactionBuilder = CreateV1Transaction(test, txPermissions);
            transactionBuilder.To(test.To);
            
            switch (txPermissions)
            {
                case ITransactionPermissionContract.TxPermissions.Basic:
                {
                    if (test.To == _contractAddress)
                    {
                        transactionBuilder.To(Address.Zero);
                    }

                    break;
                }
                case ITransactionPermissionContract.TxPermissions.Call:
                    if (test.Number == 6)
                    {
                        transactionBuilder.To(_contractAddress);
                        test.Cache = true;
                    }

                    break;
                case ITransactionPermissionContract.TxPermissions.Create:
                    if (test.Number == 6 || test.Number == 7)
                    {
                        test.Cache = true;
                    }
                    
                    transactionBuilder.To(null);
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
                new() {SenderKey = GetPrivateKey(1), ContractPermissions = ITransactionPermissionContract.TxPermissions.None, Cache = false},
                new() {SenderKey = GetPrivateKey(1), ContractPermissions = ITransactionPermissionContract.TxPermissions.All, Cache = false, GasPrice = 1},
                new() {SenderKey = GetPrivateKey(1), ContractPermissions = ITransactionPermissionContract.TxPermissions.All, Cache = false, Data = new byte[]{0, 1}},
                new() {SenderKey = GetPrivateKey(1), ContractPermissions = ITransactionPermissionContract.TxPermissions.All, Cache = false, GasPrice = 5, Data = new byte[]{0, 2, 3}},
            };

            return GetTestCases(tests, nameof(V3), CreateV3Transaction);
        }
        
        private static TransactionBuilder<Transaction> CreateV3Transaction(Test test, ITransactionPermissionContract.TxPermissions txPermissions)
        {
            var transactionBuilder = CreateV2Transaction(test, txPermissions);
            transactionBuilder.WithData(test.Data);
            transactionBuilder.WithGasPrice(test.GasPrice);
            return transactionBuilder;
        }
        
        [TestCaseSource(nameof(V3Tests))]
        public async Task<(bool IsAllowed, bool Cache)> V3(Func<Task<TestTxPermissionsBlockchain>> chainFactory, Transaction tx) => await ChainTest(chainFactory, tx, 3);

        private static TransactionBuilder<Transaction> CreateV4Transaction(Test test, ITransactionPermissionContract.TxPermissions txPermissions)
        {
            TransactionBuilder<Transaction> transactionBuilder = CreateV3Transaction(test, txPermissions);
            if (test.TxType == TxType.EIP1559)
            {
                transactionBuilder.WithMaxPriorityFeePerGas(test.GasPremium);
                transactionBuilder.WithMaxFeePerGas(test.FeeCap);
            }

            transactionBuilder.WithType(test.TxType);
            return transactionBuilder;
        }
        public static IEnumerable<TestCaseData> V4Tests()
        {
            IList<Test> tests = new List<Test>()
            {
                new() {SenderKey = GetPrivateKey(1), ContractPermissions = ITransactionPermissionContract.TxPermissions.None, Cache = false},
                new() {SenderKey = GetPrivateKey(1), ContractPermissions = ITransactionPermissionContract.TxPermissions.All, Cache = false, FeeCap = 1, TxType = TxType.EIP1559},
                new() {SenderKey = GetPrivateKey(1), ContractPermissions = ITransactionPermissionContract.TxPermissions.All, Cache = false, GasPrice = 1, TxType = TxType.Legacy},
                new() {SenderKey = GetPrivateKey(1), ContractPermissions = ITransactionPermissionContract.TxPermissions.All, Cache = false, Data = new byte[]{0, 1}},
                new() {SenderKey = GetPrivateKey(1), ContractPermissions = ITransactionPermissionContract.TxPermissions.All, Cache = false, FeeCap = 5, TxType = TxType.EIP1559, Data = new byte[]{0, 2, 3}},
                new() {SenderKey = GetPrivateKey(1), ContractPermissions = ITransactionPermissionContract.TxPermissions.All, Cache = false, GasPrice = 5, TxType = TxType.Legacy, Data = new byte[]{0, 2, 3}},
            };

            return GetTestCases(tests, nameof(V4), CreateV4Transaction);
        }
        [TestCaseSource(nameof(V4Tests))]
        public async Task<(bool IsAllowed, bool Cache)> V4(Func<Task<TestTxPermissionsBlockchain>> chainFactory, Transaction tx) => await ChainTest(chainFactory, tx, 4);
        private static async Task<(bool IsAllowed, bool Cache)> ChainTest(Func<Task<TestTxPermissionsBlockchain>> chainFactory, Transaction tx, UInt256 version)
        {
            using var chain = await chainFactory();
            var head = chain.BlockTree.Head;
            var isAllowed = chain.PermissionBasedTxFilter.IsAllowed(tx, head.Header);
            chain.TransactionPermissionContractVersions.Get(head.Header.Hash).Should().Be(version);
            return (isAllowed.Allowed, chain.TxPermissionFilterCache.Permissions.Contains((head.Hash, tx.SenderAddress)));
        }

        private static IEnumerable<TestCaseData> GetTestCases(IEnumerable<Test> tests, string testsName, Func<Test, ITransactionPermissionContract.TxPermissions, TransactionBuilder<Transaction>> transactionBuilder)
        {
            TestCaseData GetTestCase(
                Func<Task<TestTxPermissionsBlockchain>> chainFactory,
                Test test,
                ITransactionPermissionContract.TxPermissions txType)
            {
                var result = (test.ContractPermissions & txType) != ITransactionPermissionContract.TxPermissions.None;
                return new TestCaseData(chainFactory, transactionBuilder(test, txType).TestObject)
                    .SetName($"{testsName} - {test.Number}: Expected {test.ContractPermissions}, check {txType} is {result}")
                    .SetCategory(testsName + "Tests")
                    .Returns((result, test.Cache ?? true));
            }
            
            foreach (var test in tests)
            {
                foreach (var txType in TxPermissionsTypes)
                {
                    var chainTask = TestContractBlockchain.ForTest<TestTxPermissionsBlockchain, TxPermissionFilterTest>(testsName);
                    Func<Task<TestTxPermissionsBlockchain>> testFactory = async () =>
                    {
                        var chain = await chainTask;
                        chain.TxPermissionFilterCache.Permissions.Clear();
                        chain.TransactionPermissionContractVersions.Clear();
                        return chain;
                    };
                    
                    yield return GetTestCase(testFactory, test, txType);
                }
            }
        }

        private static PrivateKey GetPrivateKey(int key) => new(key.ToString("X64"));

        [TestCase(1, ExpectedResult = true)]
        [TestCase(3, ExpectedResult = true)]
        public bool allows_transactions_before_transitions(long blockNumber)
        {
            var transactionPermissionContract = new VersionedTransactionPermissionContract(AbiEncoder.Instance, 
                TestItem.AddressA,
                5, 
                Substitute.For<IReadOnlyTxProcessorSource>(), new LruCache<Keccak, UInt256>(100, "TestCache"),
                LimboLogs.Instance,
                Substitute.For<ISpecProvider>());
            
            var filter = new PermissionBasedTxFilter(transactionPermissionContract, new PermissionBasedTxFilter.Cache(), LimboLogs.Instance);
            return filter.IsAllowed(Build.A.Transaction.WithSenderAddress(TestItem.AddressB).TestObject, Build.A.BlockHeader.WithNumber(blockNumber).TestObject).Allowed;
        }

        public class TestTxPermissionsBlockchain : TestContractBlockchain
        {
            public PermissionBasedTxFilter PermissionBasedTxFilter { get; private set; }
            public PermissionBasedTxFilter.Cache TxPermissionFilterCache { get; private set; }
            
            public ICache<Keccak, UInt256> TransactionPermissionContractVersions { get; private set; }
            
            protected override BlockProcessor CreateBlockProcessor()
            {
                AuRaParameters.Validator validator = new()
                {
                    Addresses = TestItem.Addresses,
                    ValidatorType = AuRaParameters.ValidatorType.List
                };

                TransactionPermissionContractVersions =
                    new LruCache<Keccak, UInt256>(PermissionBasedTxFilter.Cache.MaxCacheSize, nameof(TransactionPermissionContract));

                var trieStore = new TrieStore(DbProvider.StateDb, LimboLogs.Instance).AsReadOnly();
                IReadOnlyTxProcessorSource txProcessorSource = new ReadOnlyTxProcessingEnv(
                    DbProvider,
                    trieStore,
                    BlockTree,
                    SpecProvider,
                    LimboLogs.Instance);

                var transactionPermissionContract = new VersionedTransactionPermissionContract(AbiEncoder.Instance, _contractAddress, 1,
                    new ReadOnlyTxProcessingEnv(DbProvider, trieStore, BlockTree, SpecProvider, LimboLogs.Instance), TransactionPermissionContractVersions, LimboLogs.Instance, SpecProvider);

                TxPermissionFilterCache = new PermissionBasedTxFilter.Cache();
                PermissionBasedTxFilter = new PermissionBasedTxFilter(transactionPermissionContract, TxPermissionFilterCache, LimboLogs.Instance);

                return new AuRaBlockProcessor(
                    SpecProvider,
                    Always.Valid,
                    new RewardCalculator(SpecProvider),
                    new BlockProcessor.BlockValidationTransactionsExecutor(TxProcessor, State),
                    State,
                    Storage,
                    ReceiptStorage,
                    LimboLogs.Instance,
                    BlockTree,
                    PermissionBasedTxFilter);
            }

            protected override async Task AddBlocksOnStart() 
            {
                await AddBlock();
                var tx = Nethermind.Core.Test.Builders.Build.A.GeneratedTransaction.WithData(new byte[]{0, 1})
                    .SignedAndResolved(GetPrivateKey(1)).WithChainId(105).WithGasPrice(0).WithValue(0).TestObject;
                await AddBlock(tx);
                await AddBlock(BuildSimpleTransaction.WithNonce(1).TestObject, BuildSimpleTransaction.WithNonce(2).TestObject);
            }
        }

        public class Test
        {
            private Address _to;
            public PrivateKey SenderKey { get; set; }
            public PrivateKey ToKey { get; set; }
            public UInt256 Value { get; set; } = 1;
            public byte[] Data { get; set; } = Bytes.Zero32;
            public UInt256 GasPrice { get; set; } = 0;
            
            public UInt256 GasPremium { get; set; } = 0;
            public UInt256 FeeCap { get; set; } = 0;
            public TxType TxType { get; set; } = TxType.Legacy;
            public Address Sender => SenderKey.Address;
            public Address To
            {
                get => _to ?? ToKey?.Address ?? Address.Zero;
                set => _to = value;
            }

            public ITransactionPermissionContract.TxPermissions ContractPermissions { get; set; }
            public bool? Cache { get; set; }
            public int Number => int.Parse(SenderKey.KeyBytes.ToHexString(), NumberStyles.HexNumber);
        }
    }
}
