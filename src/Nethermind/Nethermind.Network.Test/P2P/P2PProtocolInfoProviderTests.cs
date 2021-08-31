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

using Nethermind.Network.P2P;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class P2PProtocolInfoProviderTests
    {
        [Test]
        public void GetHighestVersionOfEthProtocol_ReturnExpectedResult()
        {
            int result = P2PProtocolInfoProvider.GetHighestVersionOfEthProtocol();
            Assert.AreEqual(66, result);
        }
        
        [Test]
        public void DefaultCapabilitiesToString_ReturnExpectedResult()
        {
            string result = P2PProtocolInfoProvider.DefaultCapabilitiesToString();
            Assert.AreEqual("eth/66,eth/65,eth/64,eth/63,eth/62", result);
        }
    }
}
