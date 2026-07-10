// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
            DataContract<int> dataContract = new(GetAll, GetFromReceipts);
            Assert.That(dataContract.IncrementalChanges, Is.True);
            Assert.That(dataContract.TryGetItemsChangedFromBlock(Build.A.BlockHeader.TestObject, [], out IEnumerable<int> items), Is.True);
            Assert.That(items, Is.EquivalentTo(new[] { 1, 5 }));
        }

        [Test]
        public void NonIncrementalChanges_check_found()
        {
            DataContract<int> dataContract = new(GetAll, TryGetFromReceiptsTrue);
            Assert.That(dataContract.IncrementalChanges, Is.False);
            Assert.That(dataContract.TryGetItemsChangedFromBlock(Build.A.BlockHeader.TestObject, [], out IEnumerable<int> items), Is.True);
            Assert.That(items, Is.EquivalentTo(new[] { 1, 5 }));
        }

        [Test]
        public void NonIncrementalChanges_check_non_found()
        {
            DataContract<int> dataContract = new(GetAll, TryGetFromReceiptsFalse);
            Assert.That(dataContract.IncrementalChanges, Is.False);
            Assert.That(dataContract.TryGetItemsChangedFromBlock(Build.A.BlockHeader.TestObject, [], out IEnumerable<int> items), Is.False);
            Assert.That(items, Is.Empty);
        }

        private static IEnumerable<int> GetAll(BlockHeader header) => new[] { 1, 5 };
        private static IEnumerable<int> GetFromReceipts(BlockHeader header, TxReceipt[] receipts) => new[] { 1, 5 };
        private static bool TryGetFromReceiptsTrue(BlockHeader header, TxReceipt[] receipts, out IEnumerable<int> items)
        {
            items = new[] { 1, 5 };
            return true;
        }

        private static bool TryGetFromReceiptsFalse(BlockHeader header, TxReceipt[] receipts, out IEnumerable<int> items)
        {
            items = Array.Empty<int>();
            return false;
        }
    }
}
