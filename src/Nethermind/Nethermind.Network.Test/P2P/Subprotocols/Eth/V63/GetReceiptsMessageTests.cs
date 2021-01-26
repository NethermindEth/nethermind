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

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [Parallelizable(ParallelScope.All)]
    public class GetReceiptsMessageTests
    {
        [Test]
        public void Sets_values_from_contructor_argument()
        {
            Keccak[] hashes = {TestItem.KeccakA, TestItem.KeccakB};
            GetReceiptsMessage message = new GetReceiptsMessage(hashes);
            Assert.AreSame(hashes, message.Hashes);
        }

        [Test]
        public void Throws_on_null_argument()
        {
            Assert.Throws<ArgumentNullException>(() => new GetReceiptsMessage(null));
        }
        
        [Test]
        public void To_string()
        {
            GetReceiptsMessage statusMessage = new GetReceiptsMessage(new List<Keccak>());
            _ = statusMessage.ToString();
        }
    }
}
