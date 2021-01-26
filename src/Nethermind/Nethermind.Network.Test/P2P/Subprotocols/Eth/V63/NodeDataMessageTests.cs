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

using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [Parallelizable(ParallelScope.All)]
    public class NodeDataMessageTests
    {
        [Test]
        public void Accepts_nulls_inside()
        {
            byte[][] data = {new byte[] {1, 2, 3}, null};
            NodeDataMessage message = new NodeDataMessage(data);
            Assert.AreSame(data, message.Data);
        }

        [Test]
        public void Accepts_nulls_top_level()
        {
            NodeDataMessage message = new NodeDataMessage(null);
            Assert.AreEqual(0, message.Data.Length);
        }

        [Test]
        public void Sets_values_from_constructor_argument()
        {
            byte[][] data = {new byte[] {1, 2, 3}, new byte[] {4, 5, 6}};
            NodeDataMessage message = new NodeDataMessage(data);
            Assert.AreSame(data, message.Data);
        }

        [Test]
        public void To_string()
        {
            NodeDataMessage statusMessage = new NodeDataMessage(new byte[][] { });
            _ = statusMessage.ToString();
        }
    }
}
