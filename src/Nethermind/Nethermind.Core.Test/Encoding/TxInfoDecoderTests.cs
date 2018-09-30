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

using Nethermind.Core.Encoding;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class TxInfoDecoderTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            TxInfo txInfo = new TxInfo();
            txInfo.BlockHash = TestObject.KeccakA;
            txInfo.BlockNumber = UInt256.One;
            txInfo.Index = 2;

            Rlp rlp = Rlp.Encode(txInfo);
            TxInfo decoded = Rlp.Decode<TxInfo>(rlp);            
            Assert.AreEqual(TestObject.KeccakA, decoded.BlockHash, "block hash");
            Assert.AreEqual(UInt256.One, decoded.BlockNumber, "number");
            Assert.AreEqual(2, decoded.Index, "index");
        }
    }
}