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

using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class ReceiptStorageTests
    {
        private ISpecProvider _specProvider;
        private IEthereumEcdsa _ethereumEcdsa;

        [SetUp]
        public void Setup()
        {
            _specProvider = RopstenSpecProvider.Instance;
            _ethereumEcdsa = new EthereumEcdsa(_specProvider, NullLogManager.Instance);
        }

        [Test]
        public void should_add_and_fetch_receipt_from_in_memory_storage()
            => TestAddAndGetReceipt(new InMemoryReceiptStorage());

        [Test]
        public void should_add_and_fetch_receipt_from_persistent_storage()
            => TestAddAndGetReceipt(new PersistentReceiptStorage(new MemDb(), NullDb.Instance, _specProvider, LimboLogs.Instance));
        
        [Test]
        public void should_add_and_fetch_receipt_from_persistent_storage_with_eip_658()
            => TestAddAndGetReceiptEip658(new PersistentReceiptStorage(new MemDb(), NullDb.Instance, _specProvider, LimboLogs.Instance));

        private void TestAddAndGetReceipt(IReceiptStorage storage)
        {
            var transaction = GetSignedTransaction();
            var receipt = GetReceipt(transaction);
            storage.Add(receipt, true);
            var fetchedReceipt = storage.Find(transaction.Hash);
            receipt.StatusCode.Should().Be(fetchedReceipt.StatusCode);
            receipt.PostTransactionState.Should().Be(fetchedReceipt.PostTransactionState);
        }
        
        private void TestAddAndGetReceiptEip658(IReceiptStorage storage)
        {
            var transaction = GetSignedTransaction();
            var receipt = GetReceipt(transaction);
            storage.Add(receipt, true);
            var fetchedReceipt = storage.Find(transaction.Hash);
            receipt.StatusCode.Should().Be(fetchedReceipt.StatusCode);
            receipt.PostTransactionState.Should().Be(fetchedReceipt.PostTransactionState);
        }

        private Transaction GetSignedTransaction(Address to = null)
            => Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, 1).TestObject;

        private static TxReceipt GetReceipt(Transaction transaction)
            => Build.A.Receipt.WithState(TestItem.KeccakB)
                .WithTransactionHash(transaction.Hash).TestObject;
    }
}