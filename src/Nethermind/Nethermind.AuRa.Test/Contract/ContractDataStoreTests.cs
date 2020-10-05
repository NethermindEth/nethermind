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
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract
{
    public class ContractDataStoreTests
    {
        [Test]
        public void returns_data_from_getAll_on_init()
        {
            TestCase<Address> testCase = TestCase<Address>.Build();
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).TestObject;
            Address[] expected = {TestItem.AddressA};
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(expected);
            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader).Should().BeEquivalentTo(expected.Cast<object>());
        }
        
        [Test]
        public void returns_cached_data_from_on_consecutive_calls()
        {
            TestCase<Address> testCase = TestCase<Address>.Build();
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).TestObject;
            Address[] expected = {TestItem.AddressA};
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(expected);
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader).Should().BeEquivalentTo(testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader));
            testCase.DataContract.Received(1).GetAllItemsFromBlock(blockHeader);
        }
        
        [Test]
        public void returns_data_from_getAll_on_non_consecutive_call()
        {
            TestCase<Address> testCase = TestCase<Address>.Build();
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(new[] {TestItem.AddressA});
            BlockHeader secondBlockHeader = Build.A.BlockHeader.WithNumber(3).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakC).TestObject;
            Address[] expected = {TestItem.AddressB};
            testCase.DataContract.GetAllItemsFromBlock(secondBlockHeader).Returns(expected);

            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlockHeader).Should().BeEquivalentTo(expected.Cast<object>());
        }
        
        [Test]
        public void returns_data_from_getAll_on_non_consecutive_receipts_with_incremental_changes()
        {
            TestCase<Address> testCase = TestCase<Address>.Build();
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(new[] {TestItem.AddressA});
            Block secondBlock = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(3).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakC).TestObject).TestObject;
            Address[] expected = {TestItem.AddressB};
            testCase.DataContract.GetAllItemsFromBlock(secondBlock.Header).Returns(expected);
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.BlockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(secondBlock, Array.Empty<TxReceipt>()));
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlock.Header).Should().BeEquivalentTo(expected.Cast<object>());
        }
        
        [Test]
        public void returns_data_from_receipts_on_non_consecutive_with_not_incremental_changes()
        {
            TestCase<Address> testCase = TestCase<Address>.Build();
            testCase.DataContract.IncrementalChanges.Returns(false);
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(new[] {TestItem.AddressA});
            Block secondBlock = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(3).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakC).TestObject).TestObject;
            Address[] expected = {TestItem.AddressB};
            testCase.DataContract.GetItemsChangedFromBlock(secondBlock.Header, Array.Empty<TxReceipt>()).Returns(expected);

            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.BlockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(secondBlock, Array.Empty<TxReceipt>()));
            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlock.Header).Should().BeEquivalentTo(expected.Cast<object>());
        }
        
        [Test]
        public void returns_data_from_getAll_on_non_consecutive_with_not_incremental_changes_if_genesis()
        {
            TestCase<Address> testCase = TestCase<Address>.Build();
            testCase.DataContract.IncrementalChanges.Returns(false);
            Block block = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(0).TestObject).TestObject;
            Address[] expected = {TestItem.AddressB};
            testCase.DataContract.GetAllItemsFromBlock(block.Header).Returns(expected);
            testCase.BlockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(block, Array.Empty<TxReceipt>()));
            testCase.ContractDataStore.GetItemsFromContractAtBlock(block.Header).Should().BeEquivalentTo(expected.Cast<object>());
        }
        
        [Test]
        public void returns_data_from_receipts_on_consecutive_with_not_incremental_changes()
        {
            TestCase<Address> testCase = TestCase<Address>.Build();
            testCase.DataContract.IncrementalChanges.Returns(false);
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(new[] {TestItem.AddressA});
            Block secondBlock = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(2).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakA).TestObject).TestObject;
            Address[] expected = {TestItem.AddressB};
            testCase.DataContract.GetItemsChangedFromBlock(secondBlock.Header, Array.Empty<TxReceipt>()).Returns(expected);
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.BlockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(secondBlock, Array.Empty<TxReceipt>()));
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlock.Header).Should().BeEquivalentTo(expected.Cast<object>());
        }
        
        [Test]
        public void returns_data_from_receipts_on_consecutive_with_incremental_changes()
        {
            TestCase<Address> testCase = TestCase<Address>.Build();
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(new[] {TestItem.AddressA});
            Block secondBlock = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(2).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakA).TestObject).TestObject;
            testCase.DataContract.GetItemsChangedFromBlock(secondBlock.Header, Array.Empty<TxReceipt>()).Returns(new[] {TestItem.AddressB});

            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.BlockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(secondBlock, Array.Empty<TxReceipt>()));
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlock.Header).Should().BeEquivalentTo(TestItem.AddressA, TestItem.AddressB);
        }
        
        [Test]
        public void returns_data_from_receipts_on_consecutive_with_incremental_changes_with_identity()
        {
            TestCase<TxPriorityContract.Destination> testCase = TestCase<TxPriorityContract.Destination>.Build(TxPriorityContract.DestinationMethodComparer.Instance);
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            testCase.DataContract.GetAllItemsFromBlock(blockHeader).Returns(
                new[]
                {
                    new TxPriorityContract.Destination(TestItem.AddressB, new byte[] {0, 1, 2, 3}, 2),
                    new TxPriorityContract.Destination(TestItem.AddressA, new byte[] {0, 1, 2, 3}, 1),
                });
            
            Block secondBlock = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(2).WithHash(TestItem.KeccakB).WithParentHash(TestItem.KeccakA).TestObject).TestObject;
            testCase.DataContract.GetItemsChangedFromBlock(secondBlock.Header, Array.Empty<TxReceipt>())
                .Returns(new[]
                {
                    new TxPriorityContract.Destination(TestItem.AddressB, new byte[] {0, 1, 2, 5}, 4),
                    new TxPriorityContract.Destination(TestItem.AddressB, new byte[] {0, 1, 2, 3}, 6)
                });

            testCase.ContractDataStore.GetItemsFromContractAtBlock(blockHeader);
            testCase.BlockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(secondBlock, Array.Empty<TxReceipt>()));
            
            testCase.ContractDataStore.GetItemsFromContractAtBlock(secondBlock.Header).Should().BeEquivalentTo(new[]
            {
                new TxPriorityContract.Destination(TestItem.AddressB, new byte[] {0, 1, 2, 3}, 6),
                new TxPriorityContract.Destination(TestItem.AddressB, new byte[] {0, 1, 2, 5}, 4),
                new TxPriorityContract.Destination(TestItem.AddressA, new byte[] {0, 1, 2, 3}, 1)
            }, o => o.ComparingByMembers<TxPriorityContract.Destination>());
        }

        public class TestCase<T>
        {
            public static TestCase<T> Build(IComparer<T> comparer = null)
            {
                var dataContract = Substitute.For<IDataContract<T>>();
                dataContract.IncrementalChanges.Returns(true);
                
                var blockProcessor = Substitute.For<IBlockProcessor>();

                return new TestCase<T>()
                {
                    DataContract = dataContract,
                    BlockProcessor = blockProcessor,
                    ContractDataStore = comparer == null
                        ? (IContractDataStore<T>)new HashSetContractDataStore<T>(dataContract, blockProcessor)
                        : new SortedListContractDataStore<T>(dataContract, blockProcessor, comparer)
                };
            }

            public IContractDataStore<T> ContractDataStore { get; private set; }

            public IBlockProcessor BlockProcessor { get; private set; }

            public IDataContract<T> DataContract { get; private set; }
        }
    }
}
