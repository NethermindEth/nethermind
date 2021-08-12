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
using System.Drawing;
using Nethermind.EthStats.Clients;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.EthStats.Test
{
    public class EthStatsClientTests
    {
        [TestCase("https://localhost/api", "wss://localhost/api")]
        [TestCase("wss://localhost/api", "wss://localhost/api")]
        [TestCase("ws://localhost/api", "ws://localhost/api")]
        [TestCase("http://localhost/api", "ws://localhost/api")]
        [TestCase("https://localhost:8000/api", "wss://localhost:8000/api")]
        public void Build_url_should_return_expected_results(string configUrl, string expectedUrl)
        {
            EthStatsClient ethClient = new(configUrl, 5000, Substitute.For<IMessageSender>(), LimboLogs.Instance);
            Assert.AreEqual(expectedUrl, ethClient.BuildUrl());
        }
        
        [TestCase("http://test://")]
        [TestCase("ftp://localhost")]
        [TestCase("http:/")]
        [TestCase("localhost")]
        public void Incorrect_url_should_throw_exception(string url)
        {
            EthStatsClient ethClient = new(url, 5000, Substitute.For<IMessageSender>(), LimboLogs.Instance);
            Assert.Throws<ArgumentException>(() => ethClient.BuildUrl());
        }
    }
}
