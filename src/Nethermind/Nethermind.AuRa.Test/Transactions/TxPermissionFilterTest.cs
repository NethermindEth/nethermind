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
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class TxPermissionFilterTest
    {
        private const string ContractAddress = "0xAB5b100cf7C8deFB3c8f3C48474223997A50fB13";
        
        public static IEnumerable<TestCaseData> V1Tests()
        {
            TestCaseData GetTestCase(
                Func<Task<TestTxPermissionsBlockchain>> test,
                PrivateKey key,
                TransactionPermissionContract.TxPermissions check,
                TransactionPermissionContract.TxPermissions expected)
            {
                var result = (expected & check) != TransactionPermissionContract.TxPermissions.None;
                return new TestCaseData(test, key, check).SetName($"Expected {expected}, check {check} is {result}").SetCategory(nameof(V1Tests)).Returns(result);
            }

            IDictionary<PrivateKey, TransactionPermissionContract.TxPermissions> expectedPermissions = new Dictionary<PrivateKey, TransactionPermissionContract.TxPermissions>
            {
                { new PrivateKey("0x0000000000000000000000000000000000000000000000000000000000000001"), TransactionPermissionContract.TxPermissions.All },
                { new PrivateKey("0x0000000000000000000000000000000000000000000000000000000000000002"), TransactionPermissionContract.TxPermissions.Basic | TransactionPermissionContract.TxPermissions.Call },
                { new PrivateKey("0x0000000000000000000000000000000000000000000000000000000000000003"), TransactionPermissionContract.TxPermissions.Basic },
                { new PrivateKey("0x0000000000000000000000000000000000000000000000000000000000000004"), TransactionPermissionContract.TxPermissions.None },
            };

            TransactionPermissionContract.TxPermissions[] checkedPermissions = new[]
            {
                TransactionPermissionContract.TxPermissions.Basic,
                TransactionPermissionContract.TxPermissions.Call,
                TransactionPermissionContract.TxPermissions.Create
            };

            var testTask = TestContractBlockchain.ForTest<TestTxPermissionsBlockchain, TxPermissionFilterTest>(nameof(V1));
            Func<Task<TestTxPermissionsBlockchain>> testFactory = () => testTask;

            foreach (var permission in expectedPermissions)
            {
                foreach (var checkedPermission in checkedPermissions)
                {
                    yield return GetTestCase(testFactory, permission.Key, checkedPermission, permission.Value);
                }
            }
        }
        
        // Contract code: https://gist.github.com/arkpar/38a87cb50165b7e683585eec71acb05a
        [TestCaseSource(nameof(V1Tests))]
        public async Task<bool> V1(Func<Task<TestTxPermissionsBlockchain>> testFactory, PrivateKey key, TransactionPermissionContract.TxPermissions txType)
        {
            Transaction CreateTransaction()
            {
                var transactionBuilder = Build.A.Transaction.WithData(null).WithSenderAddress(key.Address);
            
                switch (txType)
                {
                    case TransactionPermissionContract.TxPermissions.Call:
                        transactionBuilder.WithData(Bytes.Zero32);
                        break;
                    case TransactionPermissionContract.TxPermissions.Create:
                        transactionBuilder.WithInit(Bytes.Zero32);
                        break;
                }
            
                return transactionBuilder.TestObject;
            }
            
            var test = await testFactory();
            var head = test.BlockTree.Head;
            Transaction tx = CreateTransaction();
            return test.TxPermissionFilter.IsAllowed(tx, head.Header);
        }
        
        public static IEnumerable<TestCaseData> V2Tests()
        {
            TestCaseData GetTestCase(
                Func<Task<TestTxPermissionsBlockchain>> test,
                PrivateKey key,
                TransactionPermissionContract.TxPermissions check,
                TransactionPermissionContract.TxPermissions expected)
            {
                var result = (expected & check) != TransactionPermissionContract.TxPermissions.None;
                return new TestCaseData(test, key, check).SetName($"Expected {expected}, check {check} is {result}").SetCategory(nameof(V2Tests)).Returns(result);
            }

            IDictionary<PrivateKey, TransactionPermissionContract.TxPermissions> expectedPermissions = new Dictionary<PrivateKey, TransactionPermissionContract.TxPermissions>
            {
                { new PrivateKey("0x0000000000000000000000000000000000000000000000000000000000000001"), TransactionPermissionContract.TxPermissions.All },
                { new PrivateKey("0x0000000000000000000000000000000000000000000000000000000000000002"), TransactionPermissionContract.TxPermissions.Basic | TransactionPermissionContract.TxPermissions.Call },
                { new PrivateKey("0x0000000000000000000000000000000000000000000000000000000000000003"), TransactionPermissionContract.TxPermissions.Basic },
                { new PrivateKey("0x0000000000000000000000000000000000000000000000000000000000000004"), TransactionPermissionContract.TxPermissions.None },
            };

            TransactionPermissionContract.TxPermissions[] checkedPermissions = new[]
            {
                TransactionPermissionContract.TxPermissions.Basic,
                TransactionPermissionContract.TxPermissions.Call,
                TransactionPermissionContract.TxPermissions.Create
            };

            var testTask = TestContractBlockchain.ForTest<TestTxPermissionsBlockchain, TxPermissionFilterTest>(nameof(V1));
            Func<Task<TestTxPermissionsBlockchain>> testFactory = () => testTask;

            foreach (var permission in expectedPermissions)
            {
                foreach (var checkedPermission in checkedPermissions)
                {
                    yield return GetTestCase(testFactory, permission.Key, checkedPermission, permission.Value);
                }
            }
        }
        
        // Contract code: https://gist.github.com/VladLupashevskyi/84f18eabb1e4afadf572cf92af3e7e7f
        [TestCaseSource(nameof(V2Tests))]
        public async Task<bool> V2(Func<Task<TestTxPermissionsBlockchain>> testFactory, PrivateKey key, TransactionPermissionContract.TxPermissions txType)
        {
            Transaction CreateTransaction()
            {
                var transactionBuilder = Build.A.Transaction.WithData(null).WithSenderAddress(key.Address);
            
                switch (txType)
                {
                    case TransactionPermissionContract.TxPermissions.Call:
                        transactionBuilder.WithData(Bytes.Zero32);
                        break;
                    case TransactionPermissionContract.TxPermissions.Create:
                        transactionBuilder.WithInit(Bytes.Zero32);
                        break;
                }
            
                return transactionBuilder.TestObject;
            }
            
            var test = await testFactory();
            var head = test.BlockTree.Head;
            Transaction tx = CreateTransaction();
            return test.TxPermissionFilter.IsAllowed(tx, head.Header);
        }

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
            
            var filter = new TxPermissionFilter(transactionPermissionContract, new ITxPermissionFilter.Cache(), LimboLogs.Instance);
            return filter.IsAllowed(Build.A.Transaction.TestObject, Build.A.BlockHeader.WithNumber(blockNumber).TestObject);
        }

        public class TestTxPermissionsBlockchain : TestContractBlockchain
        {
            public TxPermissionFilter TxPermissionFilter { get; set; }
            
            protected override BlockProcessor CreateBlockProcessor()
            {
                var validator = new AuRaParameters.Validator()
                {
                    Addresses = TestItem.Addresses,
                    ValidatorType = AuRaParameters.ValidatorType.List
                };

                var transactionPermissionContract = new TransactionPermissionContract(TxProcessor, new AbiEncoder(), new Address(ContractAddress), 1,
                    new ReadOnlyTransactionProcessorSource(DbProvider, BlockTree, SpecProvider, LimboLogs.Instance));

                TxPermissionFilter = new TxPermissionFilter(transactionPermissionContract, new ITxPermissionFilter.Cache(), LimboLogs.Instance);
                
                return new AuRaBlockProcessor(SpecProvider, Always.Valid, new RewardCalculator(SpecProvider), TxProcessor, StateDb, CodeDb, State, Storage, TxPool, ReceiptStorage, LimboLogs.Instance,
                    new ListBasedValidator(validator, new ValidSealerStrategy(), LimboLogs.Instance),
                    TxPermissionFilter);
            }

            protected override Task AddBlocksOnStart() => Task.CompletedTask;
        }
    }
}