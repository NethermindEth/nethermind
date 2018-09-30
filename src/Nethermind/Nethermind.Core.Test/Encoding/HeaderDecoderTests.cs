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
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class HeaderDecoderTests
    {
        [Test]
        public void Can_decode()
        {
            BlockHeader header = Build.A.BlockHeader.TestObject;
            HeaderDecoder decoder = new HeaderDecoder();
            Rlp rlp = decoder.Encode(header);
            BlockHeader decoded = decoder.Decode(new Rlp.DecoderContext(rlp.Bytes));
            decoded.Hash = BlockHeader.CalculateHash(decoded);
            
            Assert.AreEqual(header.Hash, decoded.Hash, "hash");
        }
    }
}