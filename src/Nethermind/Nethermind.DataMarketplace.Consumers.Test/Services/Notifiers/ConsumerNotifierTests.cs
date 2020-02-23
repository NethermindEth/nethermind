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

using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.DataMarketplace.Consumers.Notifiers.Services;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Notifiers
{
    [TestFixture]
    public class ConsumerNotifierTests
    {
        private NdmNotifierMock _ndmNotifier;
        private ConsumerNotifier _notifier;

        [SetUp]
        public void Setup()
        {
            _ndmNotifier = new NdmNotifierMock();
            _notifier = new ConsumerNotifier(_ndmNotifier);
        }

        private class NdmNotifierMock : INdmNotifier
        {
            public string Type { get; set; }

            public object Data { get; set; }

            public Task NotifyAsync(Notification notification)
            {
                Type = notification.Type;
                Data = notification.Data;
                return Task.CompletedTask;
            }
        }

        [Test]
        public void _Can_send_block_processed()
        {
            _notifier.SendBlockProcessedAsync(1);
            _ndmNotifier.Type.Should().Be("block_processed");
            VerifyDataProperty("blockNumber", 1);
        }

        private void VerifyDataProperty(string propertyName, object value)
        {
            Assert.AreEqual(value, _ndmNotifier.Data.GetType().GetProperty(propertyName).GetValue(_ndmNotifier.Data));
        }
    }
}