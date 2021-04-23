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

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mev.Test
{
    [TestFixture]
    public class MevRpcModuleTests
    {
        private class MevTestBlockchain : TestBlockchain
        {
            public IMevRpcModule MevRpcModule { get; set; } = Substitute.For<IMevRpcModule>();
        }
        
        private MevTestBlockchain CreateChain()
        {
            return new MevTestBlockchain();
        }

        [Test]
        public void Can_create()
        {
            MevConfig mevConfig = new();
        }
        
        [Test]
        public void Disabled_by_default()
        {
            MevConfig mevConfig = new();
            mevConfig.Enabled.Should().BeFalse();
        }
        
        [Test]
        public void Can_enabled_and_disable()
        {
            MevConfig mevConfig = new();
            mevConfig.Enabled = true;
            mevConfig.Enabled.Should().BeTrue();
            mevConfig.Enabled = false;
            mevConfig.Enabled.Should().BeFalse();
        }

        [Test]
        public void Eth_add_bundle_works() 
        {
            // TODO?
        }
        
        [Test]
        public async Task Should_pick_more_profitable_bundle()
        {
            var chain = CreateChain();
            Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA);
            Transaction[] bundle1Txs = null;
            Transaction[] bundle2Txs = null;
            // chain.MevRpcModule.eth_sendBundle()
            // chain.MevRpcModule.eth_sendBundle()
            await chain.AddBlock();
            GetHashes(chain.BlockTree.Head.Transactions.Take(bundle2Txs.Length)).Should().Equal(GetHashes(bundle2Txs));
        }

        private static IEnumerable<Keccak?> GetHashes(IEnumerable<Transaction> bundle2Txs) => bundle2Txs.Select(t => t.Hash);
    }
}
