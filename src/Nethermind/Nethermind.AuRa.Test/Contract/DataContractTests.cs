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
using FluentAssertions;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract
{
    public class DataContractTests
    {
        [Test]
        public void IncrementalChanges()
        {
            var dataContract = new DataContract<int>(GetAll, GetFromReceipts);
            dataContract.IncrementalChanges.Should().BeTrue();
            dataContract.TryGetItemsChangedFromBlock(Build.A.BlockHeader.TestObject, Array.Empty<TxReceipt>(), out var items).Should().BeTrue();
            items.Should().BeEquivalentTo(new[] {1, 5});
        }
        
        [Test]
        public void NonIncrementalChanges_check_found()
        {
            var dataContract = new DataContract<int>(GetAll, TryGetFromReceiptsTrue);
            dataContract.IncrementalChanges.Should().BeFalse();
            dataContract.TryGetItemsChangedFromBlock(Build.A.BlockHeader.TestObject, Array.Empty<TxReceipt>(), out var items).Should().BeTrue();
            items.Should().BeEquivalentTo(new[] {1, 5});
        }
        
        [Test]
        public void NonIncrementalChanges_check_non_found()
        {
            var dataContract = new DataContract<int>(GetAll, TryGetFromReceiptsFalse);
            dataContract.IncrementalChanges.Should().BeFalse();
            dataContract.TryGetItemsChangedFromBlock(Build.A.BlockHeader.TestObject, Array.Empty<TxReceipt>(), out var items).Should().BeFalse();
            items.Should().BeEmpty();
        }

        private static IEnumerable<int> GetAll(BlockHeader header) => new[] {1, 5};
        private static IEnumerable<int> GetFromReceipts(BlockHeader header, TxReceipt[] receipts) => new[] {1, 5};
        private static bool TryGetFromReceiptsTrue(BlockHeader header, TxReceipt[] receipts, out IEnumerable<int> items)
        {
            items = new[] {1, 5};
            return true;
        }
        
        private static bool TryGetFromReceiptsFalse(BlockHeader header, TxReceipt[] receipts, out IEnumerable<int> items)
        {
            items = Array.Empty<int>();
            return false;
        }
    }
}
