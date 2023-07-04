// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract
{
    public class ContractDataStoreWithLocalDataTests : ContractDataStoreTests
    {
        [Test]
        public void assumes_empty_data_when_null()
        {
            ILocalDataSource<IEnumerable<Address>> localDataSource = Substitute.For<ILocalDataSource<IEnumerable<Address>>>();
            localDataSource.Data.Returns((IEnumerable<Address>)null);
            TestCase<Address> testCase = BuildTestCase(localDataSource, false);
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).TestObject;
            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader).Should().BeEquivalentTo(Enumerable.Empty<Address>());
        }

        [Test]
        public void returns_data_from_local_on_init()
        {
            ILocalDataSource<IEnumerable<Address>> localDataSource = Substitute.For<ILocalDataSource<IEnumerable<Address>>>();
            Address[] expected = { TestItem.AddressA };
            localDataSource.Data.Returns(expected);
            TestCase<Address> testCase = BuildTestCase(localDataSource, false);
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).TestObject;
            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader).Should().BeEquivalentTo(expected.Cast<object>());
        }

        [Test]
        public void reloads_data_from_local_on_changed()
        {
            ILocalDataSource<IEnumerable<Address>> localDataSource = Substitute.For<ILocalDataSource<IEnumerable<Address>>>();
            Address[] expected = { TestItem.AddressA, TestItem.AddressB };
            localDataSource.Data.Returns(new[] { TestItem.AddressA });
            TestCase<Address> testCase = BuildTestCase(localDataSource, false);
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).TestObject;
            localDataSource.Data.Returns(expected);
            localDataSource.Changed += Raise.Event<EventHandler>();
            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader).Should().BeEquivalentTo(expected.Cast<object>());
        }

        [Test]
        public void doesnt_reload_data_from_local_when_changed_not_fired()
        {
            ILocalDataSource<IEnumerable<Address>> localDataSource = Substitute.For<ILocalDataSource<IEnumerable<Address>>>();
            Address[] expected = { TestItem.AddressA };
            localDataSource.Data.Returns(expected);
            TestCase<Address> testCase = BuildTestCase(localDataSource, false);
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).TestObject;
            localDataSource.Data.Returns(new[] { TestItem.AddressA, TestItem.AddressB });
            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader).Should().BeEquivalentTo(expected.Cast<object>());
        }

        [Test]
        public void combines_contract_and_local_data_correctly()
        {
            ILocalDataSource<IEnumerable<Address>> localDataSource = Substitute.For<ILocalDataSource<IEnumerable<Address>>>();
            localDataSource.Data.Returns(new[] { TestItem.AddressC });
            TestCase<Address> testCase = BuildTestCase<Address>(localDataSource);
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(new[] { TestItem.AddressA });

            Address[] expected = { TestItem.AddressC, TestItem.AddressA };
            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader).Should().BeEquivalentTo(expected.Cast<object>());

            Block secondBlock = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(3).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakC).TestObject).TestObject;
            expected = new[] { TestItem.AddressC, TestItem.AddressB };
            testCase.DataContract.GetAllItemsFromBlock(secondBlock.Header).Returns(new[] { TestItem.AddressB });
            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(secondBlock));

            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlock.Header).Should().BeEquivalentTo(expected.Cast<object>());

            localDataSource.Data.Returns(new[] { TestItem.AddressC, TestItem.AddressD });
            expected = new[] { TestItem.AddressC, TestItem.AddressD, TestItem.AddressB };
            localDataSource.Changed += Raise.Event<EventHandler>();

            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlock.Header).Should().BeEquivalentTo(expected.Cast<object>());
        }

        protected override TestCase<T> BuildTestCase<T>(IComparer<T> keyComparer = null, IComparer<T> valueComparer = null) =>
            BuildTestCase(new EmptyLocalDataSource<IEnumerable<T>>(), keyComparer: keyComparer, valueComparer: valueComparer);

        private TestCase<T> BuildTestCase<T>(ILocalDataSource<IEnumerable<T>> localDataSource, bool withContractSource = true, IComparer<T> keyComparer = null, IComparer<T> valueComparer = null)
        {
            IDataContract<T> dataContract = null;
            if (withContractSource)
            {
                dataContract = Substitute.For<IDataContract<T>>();
                dataContract.IncrementalChanges.Returns(true);
            }

            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptFinder receiptsFinder = Substitute.For<IReceiptFinder>();
            receiptsFinder.Get(Arg.Any<Block>()).Returns(Array.Empty<TxReceipt>());

            return new TestCase<T>()
            {
                DataContract = dataContract,
                BlockTree = blockTree,
                ReceiptFinder = receiptsFinder,
                ContractDataStore = keyComparer is null
                    ? new ContractDataStoreWithLocalData<T>(new HashSetContractDataStoreCollection<T>(), dataContract, blockTree, receiptsFinder, LimboLogs.Instance, localDataSource)
                    : new DictionaryContractDataStore<T>(new SortedListContractDataStoreCollection<T>(keyComparer, valueComparer), dataContract, blockTree, receiptsFinder, LimboLogs.Instance, localDataSource)
            };
        }

    }
}
