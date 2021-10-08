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

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Providers.Services;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Providers.Test.Services
{
    internal class ProviderThresholdsServiceTests
    {
        private UInt256 _receiptRequestThreshold;
        private UInt256 _receiptsMergeThreshold;
        private UInt256 _paymentClaimThreshold;
        private IConfigManager _configManager;
        private NdmConfig _config;
        private ILogManager _logManager;
        private const string ConfigId = "ndm-provider";
        private IProviderThresholdsService _providerThresholdsService;

        [SetUp]
        public void Setup()
        {
            _receiptRequestThreshold = 10000000000000000;
            _receiptsMergeThreshold = 100000000000000000;
            _paymentClaimThreshold = 1000000000000000000;
            _configManager = Substitute.For<IConfigManager>();
            _config = new NdmConfig();
            _logManager = LimboLogs.Instance;
            _configManager.GetAsync(ConfigId).Returns(_config);
            _providerThresholdsService = new ProviderThresholdsService(_configManager, ConfigId, _logManager);
        }
        
        [Test]
        public void default_receipt_request_threshold_should_be_10000000000000000()
        {
            _config.ReceiptRequestThreshold.Should().Be(_receiptRequestThreshold);
        }
        
        [Test]
        public void default_receipts_merge_threshold_should_be_100000000000000000()
        {
            _config.ReceiptsMergeThreshold.Should().Be(_receiptsMergeThreshold);
        }
        
        [Test]
        public void default_payment_claim_threshold_should_be_1000000000000000000()
        {
            _config.PaymentClaimThreshold.Should().Be(_paymentClaimThreshold);
        }
        
        [Test]
        public async Task get_current_should_return_chosen_receipt_request_threshold()
        {
            await _providerThresholdsService.SetReceiptRequestAsync(9000000000000000);
            var receiptRequestThreshold = await _providerThresholdsService.GetCurrentReceiptRequestAsync();
            receiptRequestThreshold.Should().Be(_config.ReceiptRequestThreshold);
            await _configManager.Received().GetAsync(ConfigId);
        }
        
        [Test]
        public async Task get_current_should_return_chosen_receipts_merge_threshold()
        {
            await _providerThresholdsService.SetReceiptsMergeAsync(8000000000000000);
            var receiptsMergeThreshold = await _providerThresholdsService.GetCurrentReceiptsMergeAsync();
            receiptsMergeThreshold.Should().Be(_config.ReceiptsMergeThreshold);
            await _configManager.Received().GetAsync(ConfigId);
        }
        
        [Test]
        public async Task get_current_should_return_chosen_payment_claim_threshold()
        {
            await _providerThresholdsService.SetPaymentClaimAsync(7000000000000000);
            var paymentClaimThreshold = await _providerThresholdsService.GetCurrentPaymentClaimAsync();
            paymentClaimThreshold.Should().Be(_config.PaymentClaimThreshold);
            await _configManager.Received().GetAsync(ConfigId);
        }
        
        [Test]
        public void set_should_fail_if_receipt_request_threshold_will_be_0()
        {
            Func<Task> act = () => _providerThresholdsService.SetReceiptRequestAsync(0);
            act.Should().Throw<ArgumentException>()
                .WithMessage("Receipt request threshold must be greater than 0.");
        }
        
        [Test]
        public void set_should_fail_if_receipts_merge_threshold_will_be_0()
        {
            Func<Task> act = () => _providerThresholdsService.SetReceiptsMergeAsync(0);
            act.Should().Throw<ArgumentException>()
                .WithMessage("Receipts merge threshold must be greater than 0.");
        }
        
        [Test]
        public void set_should_fail_if_payment_claim_threshold_will_be_0()
        {
            Func<Task> act = () => _providerThresholdsService.SetPaymentClaimAsync(0);
            act.Should().Throw<ArgumentException>()
                .WithMessage("Payment claim threshold must be greater than 0.");
        }
    }
}