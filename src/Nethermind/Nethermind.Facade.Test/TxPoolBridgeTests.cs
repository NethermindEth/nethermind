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

using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test
{
    public class TxPoolBridgeTests
    {
        private TxPoolBridge _txPoolBridge;
        private ITxPool _txPool;
        private IWallet _wallet;

        [SetUp]
        public void SetUp()
        {
            _txPool = Substitute.For<ITxPool>();
            _wallet = Substitute.For<IWallet>();
            _txPoolBridge = new TxPoolBridge(_txPool, _wallet, Timestamper.Default, ChainId.Mainnet);
        }

        [Test]
        public void Timestamp_is_set_on_transactions()
        {
            Transaction tx = Build.A.Transaction.Signed(new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance), TestItem.PrivateKeyA).TestObject;
            _txPoolBridge.SendTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.Received().AddTransaction(Arg.Is<Transaction>(tx => tx.Timestamp != UInt256.Zero), TxHandlingOptions.PersistentBroadcast);
        }

        [Test]
        public void get_transaction_returns_null_when_transaction_not_found()
        {
            _txPoolBridge.GetPendingTransaction(TestItem.KeccakA).Should().Be(null);
        }
        
        [Test]
        public void get_transaction_returns_pending_transaction_when_found()
        {
            UInt256 nonce = 5;
            Transaction transaction = Build.A.Transaction.WithNonce(nonce).TestObject;
            transaction.Hash = transaction.CalculateHash();
            
            _txPool.TryGetPendingTransaction(transaction.Hash, out Arg.Any<Transaction>()).Returns(x =>
            {
                // x[1] is the 'out' argument that we are setting here
                x[1] = transaction;
                return true;
            });

            _txPoolBridge.GetPendingTransaction(transaction.Hash).Should().BeEquivalentTo(transaction);
        }

        [Test]
        public void get_pending_transactions_returns_tx_pool_pending_transactions()
        {
            Transaction[] transactions = Enumerable.Range(0, 10)
                .Select(i => Build.A.Transaction.WithNonce((UInt256) i).TestObject).ToArray();
            _txPool.GetPendingTransactions().Returns(transactions);
            _txPoolBridge.GetPendingTransactions().Should().BeEquivalentTo(transactions);
        }
    }
}