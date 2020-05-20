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
    }
}