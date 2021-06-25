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

using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test
{
    public class TxPoolBridgeTests
    {
        private ITxSender _txSender;
        private ITxPool _txPool;
        private ITxSigner _txSigner;

        [SetUp]
        public void SetUp()
        {
            _txPool = Substitute.For<ITxPool>();
            _txSigner = Substitute.For<ITxSigner>();
            _txSender = new TxPoolSender(_txPool, new TxSealer(_txSigner, Timestamper.Default));
        }

        [Test]
        public void Timestamp_is_set_on_transactions()
        {
            Transaction tx = Build.A.Transaction.Signed(new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance), TestItem.PrivateKeyA).TestObject;
            _txSender.SendTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.Received().SubmitTx(Arg.Is<Transaction>(tx => tx.Timestamp != UInt256.Zero), TxHandlingOptions.PersistentBroadcast);
        }

        [Test]
        public void get_transaction_returns_null_when_transaction_not_found()
        {
            _txPool.TryGetPendingTransaction(TestItem.KeccakA, out Transaction tx);
            tx.Should().Be(null);
        }
    }
}
