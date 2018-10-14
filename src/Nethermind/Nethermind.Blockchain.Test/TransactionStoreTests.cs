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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class TransactionStoreTests
    {
        private static ISpecProvider _specProvider = RopstenSpecProvider.Instance;
        private static IEthereumSigner _signer = new EthereumSigner(_specProvider, NullLogManager.Instance);
        
        [Test]
        public void Can_store_and_retrieve_receipt()
        {
            TransactionStore store = new TransactionStore(new MemDb(), RopstenSpecProvider.Instance);

            Transaction tx = Build.A.Transaction.Signed(_signer, TestObject.PrivateKeyA, 1).TestObject;
            TransactionReceipt txReceipt = Build.A.TransactionReceipt.WithState(TestObject.KeccakB).TestObject;
            store.StoreProcessedTransaction(tx.Hash, txReceipt);

            TransactionReceipt txReceiptRetrieved = store.GetReceipt(tx.Hash);
            Assert.AreEqual(txReceipt.PostTransactionState, txReceiptRetrieved.PostTransactionState, "state");
        }
    }
}