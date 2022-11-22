// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Services
{
    public class NdmDataPublisherTests
    {
        private INdmDataPublisher _dataPublisher;

        [SetUp]
        public void Setup()
        {
            _dataPublisher = new NdmDataPublisher();
        }

        [Test]
        public void publish_should_invoke_data_published_event()
        {
            object sender = null;
            NdmDataEventArgs eventArgs = null;
            var assetId = Keccak.Zero;
            var assetData = string.Empty;
            var data = new DataAssetData(assetId, assetData);
            _dataPublisher.DataPublished += (s, e) =>
            {
                sender = s;
                eventArgs = e;
            };
            _dataPublisher.Publish(data);
            sender.Should().Be(_dataPublisher);
            eventArgs.DataAssetData.Should().Be(data);
            data.AssetId.Should().Be(assetId);
            data.Data.Should().BeEquivalentTo(assetData);
        }
    }
}
