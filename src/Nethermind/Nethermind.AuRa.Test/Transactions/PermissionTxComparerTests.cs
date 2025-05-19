// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.TxPool.Collections;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class PermissionTxComparerTests
    {
        private static readonly Address[] WhitelistedSenders = new[] { TestItem.AddressC, TestItem.AddressD };

        public static IEnumerable OrderingTests
        {
            get
            {
                static Func<IEnumerable<Transaction>, IEnumerable<Transaction>> Select(Func<IEnumerable<Transaction>, IEnumerable<Transaction>> transactionSelect) =>
                    transactionSelect;


                yield return new TestCaseData(null).SetName("All");
                yield return new TestCaseData(Select(static t => t.Where(static tx => !WhitelistedSenders.Contains(tx.SenderAddress)))).SetName("Not whitelisted");
                yield return new TestCaseData(Select(static t => t.Where(static tx => WhitelistedSenders.Contains(tx.SenderAddress)))).SetName("Only whitelisted");
                yield return new TestCaseData(Select(static t => t.Where(static tx => tx.To != TestItem.AddressB))).SetName("No priority");
                yield return new TestCaseData(Select(static t => t.Where(static tx => tx.To == TestItem.AddressB))).SetName("Only priority");
            }
        }

        [TestCaseSource(nameof(OrderingTests))]
        [SuppressMessage("ReSharper", "InconsistentNaming")]
        public void order_is_correct(Func<IEnumerable<Transaction>, IEnumerable<Transaction>> transactionSelect)
        {
            IContractDataStore<Address> sendersWhitelist = Substitute.For<IContractDataStore<Address>>();
            IDictionaryContractDataStore<TxPriorityContract.Destination> priorities = Substitute.For<IDictionaryContractDataStore<TxPriorityContract.Destination>>();
            BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
            byte[] p5Signature = { 0, 1, 2, 3 };
            byte[] p6Signature = { 0, 0, 0, 2 };
            byte[] p0signature = { 0, 0, 0, 1 };
            sendersWhitelist.GetItemsFromContractAtBlock(blockHeader).Returns(WhitelistedSenders);

            SetPriority(priorities, blockHeader, TestItem.AddressB, p5Signature, 5);
            SetPriority(priorities, blockHeader, TestItem.AddressB, p6Signature, 6);

            Transaction A_B_0_10_10_P6 = Build.A.NamedTransaction(nameof(A_B_0_10_10_P6))
                .WithSenderAddress(TestItem.AddressA)
                .To(TestItem.AddressB)
                .WithNonce(0)
                .WithGasPrice(10)
                .WithGasLimit(10)
                .WithData(p6Signature)
                .TestObject;

            Transaction A_B_0_10_20_P6 = Build.A.NamedTransaction(nameof(A_B_0_10_20_P6))
                .WithSenderAddress(TestItem.AddressA)
                .To(TestItem.AddressB)
                .WithNonce(0)
                .WithGasPrice(10)
                .WithGasLimit(20)
                .WithData(p6Signature)
                .TestObject;

            Transaction A_B_0_10_5_P6 = Build.A.NamedTransaction(nameof(A_B_0_10_5_P6))
                .WithSenderAddress(TestItem.AddressA)
                .To(TestItem.AddressB)
                .WithNonce(0)
                .WithGasPrice(10)
                .WithGasLimit(5)
                .WithData(p6Signature)
                .TestObject;

            Transaction A_B_0_10_10_P5 = Build.A.NamedTransaction(nameof(A_B_0_10_10_P5))
                .WithSenderAddress(TestItem.AddressA)
                .To(TestItem.AddressB)
                .WithNonce(0)
                .WithGasPrice(10)
                .WithGasLimit(10)
                .WithData(p5Signature)
                .TestObject;

            Transaction A_B_0_20_10_P5 = Build.A.NamedTransaction(nameof(A_B_0_20_10_P5))
                .WithSenderAddress(TestItem.AddressA)
                .To(TestItem.AddressB)
                .WithNonce(0)
                .WithGasPrice(20)
                .WithGasLimit(10)
                .WithData(p5Signature)
                .TestObject;

            Transaction A_B_1_20_10_P5 = Build.A.NamedTransaction(nameof(A_B_1_20_10_P5))
                .WithSenderAddress(TestItem.AddressA)
                .To(TestItem.AddressB)
                .WithNonce(1)
                .WithGasPrice(20)
                .WithGasLimit(10)
                .WithData(p5Signature)
                .TestObject;

            Transaction A_B_1_200_10_P0 = Build.A.NamedTransaction(nameof(A_B_1_200_10_P0))
                .WithSenderAddress(TestItem.AddressA)
                .To(TestItem.AddressB)
                .WithNonce(1)
                .WithGasPrice(200)
                .WithGasLimit(10)
                .WithData(p0signature)
                .TestObject;

            Transaction A_A_0_100_100_P0 = Build.A.NamedTransaction(nameof(A_A_0_100_100_P0))
                .WithSenderAddress(TestItem.AddressA)
                .To(TestItem.AddressA)
                .WithNonce(0)
                .WithGasPrice(100)
                .WithGasLimit(100)
                .WithData(p5Signature)
                .TestObject;

            Transaction A_A_1_100_100_P0 = Build.A.NamedTransaction(nameof(A_A_1_100_100_P0))
                .WithSenderAddress(TestItem.AddressA)
                .To(TestItem.AddressA)
                .WithNonce(1)
                .WithGasPrice(100)
                .WithGasLimit(100)
                .WithData(p5Signature)
                .TestObject;

            Transaction A_A_1_1000_1000_P0 = Build.A.NamedTransaction(nameof(A_A_1_1000_1000_P0))
                .WithSenderAddress(TestItem.AddressA)
                .To(TestItem.AddressA)
                .WithNonce(1)
                .WithGasPrice(1000)
                .WithGasLimit(1000)
                .WithData(p5Signature)
                .TestObject;

            Transaction A_A_2_1000_1_P0 = Build.A.NamedTransaction(nameof(A_A_2_1000_1_P0))
                .WithSenderAddress(TestItem.AddressA)
                .To(TestItem.AddressA)
                .WithNonce(2)
                .WithGasPrice(1000)
                .WithGasLimit(1)
                .WithData(p5Signature)
                .TestObject;

            Transaction B_B_2_1000_1_P6 = Build.A.NamedTransaction(nameof(B_B_2_1000_1_P6))
                .WithSenderAddress(TestItem.AddressB)
                .To(TestItem.AddressB)
                .WithNonce(2)
                .WithGasPrice(1000)
                .WithGasLimit(1)
                .WithData(p6Signature)
                .TestObject;

            Transaction B_B_2_15_1_P5 = Build.A.NamedTransaction(nameof(B_B_2_15_1_P5))
                .WithSenderAddress(TestItem.AddressB)
                .To(TestItem.AddressB)
                .WithNonce(2)
                .WithGasPrice(15)
                .WithGasLimit(1)
                .WithData(p5Signature)
                .TestObject;

            Transaction B_B_3_15_1_P6 = Build.A.NamedTransaction(nameof(B_B_3_15_1_P6))
                .WithSenderAddress(TestItem.AddressB)
                .To(TestItem.AddressB)
                .WithNonce(3)
                .WithGasPrice(15)
                .WithGasLimit(1)
                .WithData(p6Signature)
                .TestObject;

            Transaction C_B_3_10_1_P6_W = Build.A.NamedTransaction(nameof(C_B_3_10_1_P6_W))
                .WithSenderAddress(TestItem.AddressC)
                .To(TestItem.AddressB)
                .WithNonce(3)
                .WithGasPrice(10)
                .WithGasLimit(1)
                .WithData(p6Signature)
                .TestObject;

            Transaction C_B_4_10_1_P0_W = Build.A.NamedTransaction(nameof(C_B_4_10_1_P0_W))
                .WithSenderAddress(TestItem.AddressC)
                .To(TestItem.AddressB)
                .WithNonce(4)
                .WithGasPrice(10)
                .WithGasLimit(1)
                .TestObject;

            Transaction D_B_4_100_1_P0_W = Build.A.NamedTransaction(nameof(D_B_4_100_1_P0_W))
                .WithSenderAddress(TestItem.AddressD)
                .To(TestItem.AddressB)
                .WithNonce(4)
                .WithGasPrice(100)
                .WithGasLimit(1)
                .TestObject;

            IEnumerable<Transaction> transactions = new[]
            {
                A_B_1_200_10_P0,
                A_A_2_1000_1_P0,
                A_A_1_1000_1000_P0,
                C_B_4_10_1_P0_W,
                A_A_1_100_100_P0,
                A_A_0_100_100_P0,
                A_B_0_10_10_P5,
                D_B_4_100_1_P0_W,
                C_B_3_10_1_P6_W,
                A_B_0_10_10_P6,
                A_B_0_20_10_P5,
                B_B_2_15_1_P5,
                A_B_0_10_5_P6,
                B_B_3_15_1_P6,
                A_B_0_10_20_P6,
                A_B_1_20_10_P5,
                B_B_2_1000_1_P6
            };

            IEnumerable<Transaction> expectation = new[]
            {
                C_B_3_10_1_P6_W,
                D_B_4_100_1_P0_W,
                C_B_4_10_1_P0_W,
                B_B_2_1000_1_P6,
                A_B_0_10_5_P6,
                A_B_0_10_10_P6,
                A_B_0_10_20_P6,
                A_B_0_20_10_P5,
                B_B_2_15_1_P5,
                B_B_3_15_1_P6,
                A_B_0_10_10_P5,
                A_A_0_100_100_P0,
                A_B_1_20_10_P5,
                A_A_1_1000_1000_P0,
                A_B_1_200_10_P0,
                A_A_1_100_100_P0,
                A_A_2_1000_1_P0
            };

            transactions = transactionSelect?.Invoke(transactions) ?? transactions;
            expectation = transactionSelect?.Invoke(expectation) ?? expectation;

            IBlockTree blockTree = Substitute.For<IBlockTree>();
            Block block = Build.A.Block.WithNumber(0).TestObject;
            blockTree.Head.Returns(block);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            var spec = new ReleaseSpec() { IsEip1559Enabled = false };
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
            TransactionComparerProvider transactionComparerProvider = new(specProvider, blockTree);
            IComparer<Transaction> defaultComparer = transactionComparerProvider.GetDefaultComparer();
            IComparer<Transaction> comparer = new CompareTxByPriorityOnSpecifiedBlock(sendersWhitelist, priorities, blockHeader)
                .ThenBy(defaultComparer);


            Dictionary<AddressAsKey, Transaction[]> txBySender = transactions.GroupBy(t => t.SenderAddress)
                .ToDictionary(
                    g => (AddressAsKey)g.Key,
                    g => g.OrderBy(t => t,
                        // to simulate order coming from TxPool
                        comparer.GetPoolUniqueTxComparerByNonce()).ToArray());

            const int DefaultGasLimit = 36_000_000;

            Transaction[] orderedTransactions = TxPoolTxSource.Order(txBySender, comparer, _ => true, DefaultGasLimit).ToArray();
            orderedTransactions.Should().BeEquivalentTo(expectation, o => o.WithStrictOrdering());
        }

        private static void SetPriority(
            IDictionaryContractDataStore<TxPriorityContract.Destination> priorities,
            BlockHeader blockHeader,
            Address target,
            byte[] prioritizedFnSignature,
            UInt256 value)
        {
            priorities.TryGetValue(blockHeader,
                    Arg.Is<TxPriorityContract.Destination>(d => d.Target == target && Bytes.AreEqual(d.FnSignature, prioritizedFnSignature)),
                    out Arg.Any<TxPriorityContract.Destination>())
                .Returns(x =>
                {
                    x[2] = new TxPriorityContract.Destination(target, prioritizedFnSignature, value);
                    return true;
                });
        }
    }
}
