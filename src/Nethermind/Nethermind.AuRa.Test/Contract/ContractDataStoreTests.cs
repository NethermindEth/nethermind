﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract
{
    public class ContractDataStoreTests
    {
        [Test]
        public void returns_data_from_getAll_on_init()
        {
            TestCase<Address> testCase = BuildTestCase<Address>();
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).TestObject;
            Address[] expected = {TestItem.AddressA};
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(expected);
            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader).Should().BeEquivalentTo(expected.Cast<object>());
        }
        
        [Test]
        public void returns_cached_data_from_on_consecutive_calls()
        {
            TestCase<Address> testCase = BuildTestCase<Address>();
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).TestObject;
            Address[] expected = {TestItem.AddressA};
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(expected);
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader).Should().BeEquivalentTo(testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader));
            testCase.DataContract.Received(1).GetAllItemsFromBlock(blockHeader);
        }
        
        [Test]
        public void returns_data_from_getAll_on_non_consecutive_call()
        {
            TestCase<Address> testCase = BuildTestCase<Address>();
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(new[] {TestItem.AddressA});
            BlockHeader secondBlockHeader = Build.A.BlockHeader.WithNumber(3).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakC).TestObject;
            Address[] expected = {TestItem.AddressB};
            testCase.DataContract.GetAllItemsFromBlock(secondBlockHeader).Returns(expected);

            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlockHeader).Should().BeEquivalentTo(expected.Cast<object>());
        }
        
        [Test]
        public void returns_data_from_previous_block_on_error()
        {
            TestCase<Address> testCase = BuildTestCase<Address>();
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            Address[] expected = {TestItem.AddressA};
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(expected);
            BlockHeader secondBlockHeader = Build.A.BlockHeader.WithNumber(3).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakC).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(secondBlockHeader).Throws(new AbiException(string.Empty));

            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlockHeader).Should().BeEquivalentTo(expected.Cast<object>());
        }
        
        [Test]
        public void returns_data_from_getAll_on_non_consecutive_receipts_with_incremental_changes()
        {
            TestCase<Address> testCase = BuildTestCase<Address>();
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(new[] {TestItem.AddressA});
            Block secondBlock = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(3).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakC).TestObject).TestObject;
            Address[] expected = {TestItem.AddressB};
            testCase.DataContract.GetAllItemsFromBlock(secondBlock.Header).Returns(expected);
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(secondBlock));
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlock.Header).Should().BeEquivalentTo(expected.Cast<object>());
        }
        
        [Test]
        public async Task returns_data_from_receipts_on_non_consecutive_with_not_incremental_changes()
        {
            TestCase<Address> testCase = BuildTestCase<Address>();
            testCase.DataContract.IncrementalChanges.Returns(false);
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(new[] {TestItem.AddressA});
            Block secondBlock = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(3).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakC).TestObject).TestObject;
            Address[] expected = {TestItem.AddressB};
            testCase.DataContract.TryGetItemsChangedFromBlock(secondBlock.Header, Array.Empty<TxReceipt>(), out Arg.Any<IEnumerable<Address>>())
                .Returns(x =>
                {
                    x[2] = expected;
                    return true;
                });

            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(secondBlock));
            
            await Task.Delay(10); // delay for refresh from contract as its async
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlock.Header).Should().BeEquivalentTo(expected.Cast<object>());
        }
        
        [Test]
        public void returns_data_from_getAll_on_non_consecutive_with_not_incremental_changes_if_genesis()
        {
            TestCase<Address> testCase = BuildTestCase<Address>();
            testCase.DataContract.IncrementalChanges.Returns(false);
            Block block = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(0).TestObject).TestObject;
            Address[] expected = {TestItem.AddressB};
            testCase.DataContract.GetAllItemsFromBlock(block.Header).Returns(expected);
            testCase.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(block));
            testCase.ContractDataStore.GetItemsFromContractAtBlock(block.Header).Should().BeEquivalentTo(expected.Cast<object>());
        }
        
        [Test]
        public async Task returns_data_from_receipts_on_consecutive_with_not_incremental_changes()
        {
            TestCase<Address> testCase = BuildTestCase<Address>();
            testCase.DataContract.IncrementalChanges.Returns(false);
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(new[] {TestItem.AddressA});
            Block secondBlock = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(2).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakA).TestObject).TestObject;
            Address[] expected = {TestItem.AddressB};
            testCase.DataContract.TryGetItemsChangedFromBlock(secondBlock.Header, Array.Empty<TxReceipt>(), out Arg.Any<IEnumerable<Address>>())
                .Returns(x =>
                {
                    x[2] = expected;
                    return true;
                });
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(secondBlock));

            await Task.Delay(10); // delay for refresh from contract as its async
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlock.Header).Should().BeEquivalentTo(expected.Cast<object>());
        }
        
        [Test]
        public async Task returns_data_from_receipts_on_consecutive_with_incremental_changes()
        {
            TestCase<Address> testCase = BuildTestCase<Address>();
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(new[] {TestItem.AddressA});
            Block secondBlock = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(2).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakA).TestObject).TestObject;
            testCase.DataContract.TryGetItemsChangedFromBlock(secondBlock.Header, Array.Empty<TxReceipt>(), out Arg.Any<IEnumerable<Address>>())
                .Returns(x =>
                {
                    x[2] = new[] {TestItem.AddressB};
                    return true;
                });

            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(secondBlock));

            await Task.Delay(10); // delay for refresh from contract as its async
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlock.Header).Should().BeEquivalentTo(TestItem.AddressA, TestItem.AddressB);
        }
        
        [Test]
        public async Task returns_unmodified_data_from_empty_receipts_on_consecutive_with_incremental_changes()
        {
            TestCase<Address> testCase = BuildTestCase<Address>();
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(new[] {TestItem.AddressA, TestItem.AddressC});
            Block secondBlock = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(2).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakA).TestObject).TestObject;
            testCase.DataContract.TryGetItemsChangedFromBlock(secondBlock.Header, Array.Empty<TxReceipt>(), out Arg.Any<IEnumerable<Address>>())
                .Returns(x =>
                {
                    x[2] = Array.Empty<Address>();
                    return false;
                });

            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(secondBlock));
            
            await Task.Delay(10); // delay for refresh from contract as its async
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlock.Header).Should().BeEquivalentTo(TestItem.AddressA, TestItem.AddressC);
        }
        
        [Test]
        public async Task returns_data_from_receipts_on_consecutive_with_incremental_changes_with_identity()
        {
            TestCase<TxPriorityContract.Destination> testCase = BuildTestCase(
                TxPriorityContract.DistinctDestinationMethodComparer.Instance, 
                TxPriorityContract.ValueDestinationMethodComparer.Instance);
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(
                new[]
                {
                    new TxPriorityContract.Destination(TestItem.AddressB, new byte[] {0, 1, 2, 3}, 2),
                    new TxPriorityContract.Destination(TestItem.AddressA, new byte[] {0, 1, 2, 3}, 1),
                });
            
            Block secondBlock = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(2).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakA).TestObject).TestObject;
            testCase.DataContract.TryGetItemsChangedFromBlock(secondBlock.Header, Array.Empty<TxReceipt>()
                    , out Arg.Any<IEnumerable<TxPriorityContract.Destination>>())
                .Returns(x =>
                {
                    x[2] = new[]
                    {
                        new TxPriorityContract.Destination(TestItem.AddressB, new byte[] {0, 1, 2, 5}, 4), 
                        new TxPriorityContract.Destination(TestItem.AddressB, new byte[] {0, 1, 2, 3}, 6)
                    };
                    return true;
                });

            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(secondBlock));
            
            await Task.Delay(10); // delay for refresh from contract as its async
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlock.Header).Should().BeEquivalentTo(new[]
            {
                new TxPriorityContract.Destination(TestItem.AddressB, new byte[] {0, 1, 2, 3}, 6),
                new TxPriorityContract.Destination(TestItem.AddressB, new byte[] {0, 1, 2, 5}, 4),
                new TxPriorityContract.Destination(TestItem.AddressA, new byte[] {0, 1, 2, 3}, 1)
            }, o => o.ComparingByMembers<TxPriorityContract.Destination>());
        }

        protected virtual TestCase<T> BuildTestCase<T>(IComparer<T> keyComparer = null, IComparer<T> valueComparer = null)
        {
            var dataContract = Substitute.For<IDataContract<T>>();
            dataContract.IncrementalChanges.Returns(true);
                
            var blockTree = Substitute.For<IBlockTree>();
            var receiptsFinder = Substitute.For<IReceiptFinder>();
            receiptsFinder.Get(Arg.Any<Block>()).Returns(Array.Empty<TxReceipt>());

            return new TestCase<T>()
            {
                DataContract = dataContract,
                BlockTree = blockTree,
                ReceiptFinder = receiptsFinder,
                ContractDataStore = keyComparer == null
                    ? (IContractDataStore<T>)new ContractDataStore<T>(new HashSetContractDataStoreCollection<T>(), dataContract, blockTree, receiptsFinder, LimboLogs.Instance)
                    : new DictionaryContractDataStore<T>(new SortedListContractDataStoreCollection<T>(keyComparer, valueComparer), dataContract, blockTree, receiptsFinder, LimboLogs.Instance)
            };
        }

        public class TestCase<T>
        {
            public IContractDataStore<T> ContractDataStore { get; set; }

            public IBlockTree BlockTree { get; set; }
            
            public IReceiptFinder ReceiptFinder { get; set; }

            public IDataContract<T> DataContract { get; set; }
        }
    }
}
