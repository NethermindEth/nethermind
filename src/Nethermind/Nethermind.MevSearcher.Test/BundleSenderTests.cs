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
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Nethermind.MevSearcher.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.Wallet;
using Newtonsoft.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.MevSearcher.Test
{
    [TestFixture]
    public class BundleSenderTests
    {
        [Test]
        public void Should_serialize_properly()
        {
            Transaction tx1 = Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx2 = Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            MevBundle bundle = new MevBundle(1, new []{tx1, tx2});

            string serializedRequest = bundle.GenerateSerializedSendBundleRequest();

            string expectedRequest = $"{{\"jsonrpc\":\"2.0\",\"method\":\"eth_sendBundle\",\"params\":[{{\"txs\":[\"{Rlp.Encode(tx1)}\",\"{Rlp.Encode(tx2)}\"],\"blockNumber\":\"{bundle.BlockNumber}\"}}],\"id\":67}}";
            
            serializedRequest.Should().Be(expectedRequest);
        }
        
        [Test]
        public void Should_sign_correctly()
        {
            Transaction tx1 = Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx2 = Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            MevBundle bundle = new MevBundle(1, new []{tx1, tx2});

            string serializedRequest = bundle.GenerateSerializedSendBundleRequest();
            Signature signature = BundleSender.SignMessage(serializedRequest, new Signer(ChainId.Mainnet, TestItem.PrivateKeyA, NullLogManager.Instance));
            
            signature.ToString().Should().Be("0x2aa7aefdfb45163cd923052a5caaf444379a8c7a2d1002c8e360cbd7a41ac0b32079109e92557d3a7750bdb817eead0289953dc2feee959c488acb2c081a43f31b");
        }
    }
}
