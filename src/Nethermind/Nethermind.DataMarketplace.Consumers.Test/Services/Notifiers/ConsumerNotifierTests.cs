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

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Notifiers.Services;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Core.Services.Models;
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
        public void Can_send_block_processed()
        {
            _notifier.SendBlockProcessedAsync(1);
            _ndmNotifier.Type.Should().Be("block_processed");
            VerifyDataProperty("blockNumber", 1);
        }

        [Test]
        public void Can_send_claimed_refund()
        {
            _notifier.SendClaimedRefundAsync(TestItem.KeccakA, "data", TestItem.KeccakB);
            _ndmNotifier.Type.Should().Be("claimed_refund");
            VerifyDataProperty("depositId", TestItem.KeccakA);
            VerifyDataProperty("dataAssetName", "data");
            VerifyDataProperty("transactionHash", TestItem.KeccakB);
        }

        [Test]
        public void Can_send_data_request_result()
        {
            _notifier.SendDataRequestResultAsync(TestItem.KeccakA, DataRequestResult.KycUnconfirmed);
            _ndmNotifier.Type.Should().Be("data_request_result");
            VerifyDataProperty("depositId", TestItem.KeccakA);
            VerifyDataProperty("result", "KycUnconfirmed");
        }

        [Test]
        public void Can_send_gas_price()
        {
            _notifier.SendGasPriceAsync(new GasPriceTypes(GasPriceDetails.Empty, GasPriceDetails.Empty, GasPriceDetails.Empty, GasPriceDetails.Empty, GasPriceDetails.Empty, "type", 1));
            _ndmNotifier.Type.Should().Be("gas_price");
            VerifyDataProperty("type", "type");
            VerifyDataProperty("updatedAt", 1);
        }

        [Test]
        public void Can_send_eth_usd_price()
        {
            const string currency = "USDT_ETH";
            _notifier.SendUsdPriceAsync(currency, 120.01m, 1);
            _ndmNotifier.Type.Should().Be("usd_price");
            VerifyDataProperty("price", 120.01m);
            VerifyDataProperty("updatedAt", 1);
        }

        [Test]
        public void Can_send_data_invalid()
        {
            _notifier.SendDataInvalidAsync(TestItem.KeccakA, InvalidDataReason.InternalError);
            _ndmNotifier.Type.Should().Be("data_invalid");
            VerifyDataProperty("depositId", TestItem.KeccakA);
            VerifyDataProperty("reason", "InternalError");
        }

        [Test]
        public void Can_send_deposit_rejected()
        {
            _notifier.SendDepositRejectedAsync(TestItem.KeccakA);
            _ndmNotifier.Type.Should().Be("deposit_rejected");
            VerifyDataProperty("depositId", TestItem.KeccakA);
        }

        [Test]
        public void Can_send_data_stream_disabled()
        {
            _notifier.SendDataStreamDisabledAsync(TestItem.KeccakA, TestItem.KeccakB);
            _ndmNotifier.Type.Should().Be("data_stream_disabled");
            VerifyDataProperty("depositId", TestItem.KeccakA);
            VerifyDataProperty("sessionId", TestItem.KeccakB);
        }

        [Test]
        public void Can_send_claimed_early_refund()
        {
            _notifier.SendClaimedEarlyRefundAsync(TestItem.KeccakA, "asset", TestItem.KeccakB);
            _ndmNotifier.Type.Should().Be("claimed_early_refund");
            VerifyDataProperty("depositId", TestItem.KeccakA);
            VerifyDataProperty("dataAssetName", "asset");
            VerifyDataProperty("transactionHash", TestItem.KeccakB);
        }

        [Test]
        public void Can_send_deposit_confirmations_status()
        {
            _notifier.SendDepositConfirmationsStatusAsync(TestItem.KeccakA, "asset", 5, 6, 1, false);
            _ndmNotifier.Type.Should().Be("deposit_confirmations");
            VerifyDataProperty("depositId", TestItem.KeccakA);
            VerifyDataProperty("dataAssetName", "asset");
            VerifyDataProperty("confirmations", 5);
            VerifyDataProperty("requiredConfirmations", 6);
            VerifyDataProperty("confirmationTimestamp", 1);
            VerifyDataProperty("confirmed", false);
        }
        
        [Test]
        public void Can_send_grace_units_exceeded()
        {
            _notifier.SendGraceUnitsExceeded(TestItem.KeccakA, "asset", 1, 2, 3);
            _ndmNotifier.Type.Should().Be("grace_units_exceeded");
            VerifyDataProperty("depositId", TestItem.KeccakA);
            VerifyDataProperty("dataAssetName", "asset");
            VerifyDataProperty("consumedUnitsFromProvider", 1);
            VerifyDataProperty("consumedUnits", 2);
            VerifyDataProperty("graceUnits", 3);
        }
        
        [Test]
        public void Can_send_data_stream_enabled()
        {
            _notifier.SendDataStreamEnabledAsync(TestItem.KeccakA, TestItem.KeccakB);
            _ndmNotifier.Type.Should().Be("data_stream_enabled");
            VerifyDataProperty("depositId", TestItem.KeccakA);
            VerifyDataProperty("sessionId", TestItem.KeccakB);
        }
        
        [Test]
        public void Can_send_consumer_address_changed()
        {
            _notifier.SendConsumerAddressChangedAsync(TestItem.AddressA, TestItem.AddressB);
            _ndmNotifier.Type.Should().Be("consumer_address_changed");
            VerifyDataProperty("newAddress", TestItem.AddressA);
            VerifyDataProperty("previousAddress", TestItem.AddressB);
        }
        
        [Test]
        public void Can_send_data_asset_state_changed()
        {
            _notifier.SendDataAssetStateChangedAsync(TestItem.KeccakA, "asset", DataAssetState.Archived);
            _ndmNotifier.Type.Should().Be("data_asset_state_changed");
            VerifyDataProperty("id", TestItem.KeccakA);
            VerifyDataProperty("name", "asset");
            VerifyDataProperty("state", "Archived");
        }
        
        [Test]
        public void Can_send_provider_address()
        {
            _notifier.SendProviderAddressChangedAsync(TestItem.AddressA, TestItem.AddressB);
            _ndmNotifier.Type.Should().Be("provider_address_changed");
            VerifyDataProperty("newAddress", TestItem.AddressA);
            VerifyDataProperty("previousAddress", TestItem.AddressB);
        }
        
        [Test]
        public void Can_send_data_availability_changed()
        {
            _notifier.SendDataAvailabilityChangedAsync(TestItem.KeccakA, TestItem.KeccakB, DataAvailability.UnitsExceeded);
            _ndmNotifier.Type.Should().Be("data_availability_changed");
            VerifyDataProperty("depositId", TestItem.KeccakA);
            VerifyDataProperty("sessionId", TestItem.KeccakB);
            VerifyDataProperty("availability", "UnitsExceeded");
        }
        
        [Test]
        public void Can_send_data_asset_removed()
        {
            _notifier.SendDataAssetRemovedAsync(TestItem.KeccakA, "asset");
            _ndmNotifier.Type.Should().Be("data_asset_removed");
            VerifyDataProperty("id", TestItem.KeccakA);
            VerifyDataProperty("name", "asset");
        }
        
        [Test]
        public void Can_send_consumer_account_locked()
        {
            _notifier.SendConsumerAccountLockedAsync(TestItem.AddressA);
            _ndmNotifier.Type.Should().Be("consumer_account_locked");
            VerifyDataProperty("address", TestItem.AddressA);
        }
        
        [Test]
        public void Can_send_consumer_account_unlocked()
        {
            _notifier.SendConsumerAccountUnlockedAsync(TestItem.AddressA);
            _ndmNotifier.Type.Should().Be("consumer_account_unlocked");
            VerifyDataProperty("address", TestItem.AddressA);
        }

        private void VerifyDataProperty(string propertyName, object value)
        {
            Assert.AreEqual(value, _ndmNotifier.Data.GetType().GetProperty(propertyName).GetValue(_ndmNotifier.Data));
        }
    }
}
