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


using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery
{
    [TestFixture]
    public class NetworkHelperTests
    {
        [Test]
        public void ExternalIpTest()
        {
            var networkHelper = new NetworkHelper(NullLogger.Instance);
            var address = networkHelper.GetExternalIp();
            Assert.IsNotNull(address);
        }

        [Test]
        public void InternalIpTest()
        {
            var networkHelper = new NetworkHelper(NullLogger.Instance);
            var address = networkHelper.GetLocalIp();
            Assert.IsNotNull(address);
        }
    }
}