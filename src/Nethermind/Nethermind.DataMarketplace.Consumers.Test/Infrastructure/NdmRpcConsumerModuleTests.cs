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
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Domain;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc;
using Nethermind.DataMarketplace.Consumers.Queries;
using Nethermind.DataMarketplace.Consumers.Services;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Facade;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure
{
    public class NdmRpcConsumerModuleTests
    {
        private IConsumerService _consumerService;
        private IReportService _reportService;
        private IJsonRpcNdmConsumerChannel _jsonRpcNdmConsumerChannel;
        private IEthRequestService _ethRequestService;
        private IPersonalBridge _personalBridge;
        private INdmRpcConsumerModule _rpc;

        [SetUp]
        public void Setup()
        {
            _consumerService = Substitute.For<IConsumerService>();
            _reportService = Substitute.For<IReportService>();
            _jsonRpcNdmConsumerChannel = Substitute.For<IJsonRpcNdmConsumerChannel>();
            _ethRequestService = Substitute.For<IEthRequestService>();
            _personalBridge = Substitute.For<IPersonalBridge>();
            _rpc = new NdmRpcConsumerModule(_consumerService, _reportService, _jsonRpcNdmConsumerChannel,
                _ethRequestService, _personalBridge, LimboLogs.Instance);
        }

        [Test]
        public void given_personal_bridge_list_accounts_should_return_accounts()
        {
            _personalBridge.ListAccounts().Returns(new[] {TestItem.AddressA});
            var result = _rpc.ndm_listAccounts();
            _personalBridge.Received().ListAccounts();
            result.Data.Should().ContainSingle();
        }

        [Test]
        public void given_null_personal_bridge_list_accounts_should_not_return_accounts()
        {
            _personalBridge = null;
            _rpc = new NdmRpcConsumerModule(_consumerService, _reportService, _jsonRpcNdmConsumerChannel,
                _ethRequestService, _personalBridge, LimboLogs.Instance);
            var result = _rpc.ndm_listAccounts();
            result.Data.Should().BeEmpty();
        }

        [Test]
        public void get_consumer_address_should_return_address()
        {
            _consumerService.GetAddress().Returns(TestItem.AddressA);
            var result = _rpc.ndm_getConsumerAddress();
            _consumerService.Received().GetAddress();
            result.Data.Should().Be(TestItem.AddressA);
        }

        [Test]
        public async Task change_consumer_address_should_return_changed_address()
        {
            var result = await _rpc.ndm_changeConsumerAddress(TestItem.AddressA);
            await _consumerService.Received().ChangeAddressAsync(TestItem.AddressA);
            result.Data.Should().Be(TestItem.AddressA);
        }

        [Test]
        public void get_discovered_data_headers_should_return_data_headers()
        {
            _consumerService.GetDiscoveredDataHeaders().Returns(new List<DataHeader> {GetDataHeader()});
            var result = _rpc.ndm_getDiscoveredDataHeaders();
            _consumerService.Received().GetDiscoveredDataHeaders();
            result.Data.Should().NotBeEmpty();
        }

        [Test]
        public async Task get_known_data_headers_should_return_data_header_info()
        {
            _consumerService.GetKnownDataHeadersAsync()
                .Returns(new[] {new DataHeaderInfo(Keccak.Zero, "test", "test")});
            var result = await _rpc.ndm_getKnownDataHeaders();
            await _consumerService.Received().GetKnownDataHeadersAsync();
            result.Data.Should().NotBeEmpty();
        }

        [Test]
        public async Task get_known_providers_should_return_providers_info()
        {
            _consumerService.GetKnownProvidersAsync().Returns(new[] {new ProviderInfo("test", TestItem.AddressA)});
            var result = await _rpc.ndm_getKnownProviders();
            await _consumerService.Received().GetKnownProvidersAsync();
            result.Data.Should().NotBeEmpty();
        }

        [Test]
        public void get_connected_providers_should_return_providers_addresses()
        {
            _consumerService.GetConnectedProviders().Returns(new[] {TestItem.AddressA});
            var result = _rpc.ndm_getConnectedProviders();
            _consumerService.Received().GetConnectedProviders();
            result.Data.Should().NotBeEmpty();
        }

        [Test]
        public async Task get_deposits_should_return_paged_results_of_deposits()
        {
            var query = new GetDeposits();
            _consumerService.GetDepositsAsync(query)
                .Returns(PagedResult<DepositDetails>.Create(new[] {GetDepositDetails()}, 1, 1, 1, 1));
            var result = await _rpc.ndm_getDeposits(query);
            await _consumerService.Received().GetDepositsAsync(query);
            result.Data.Items.Should().NotBeEmpty();
            result.Data.Page.Should().Be(1);
            result.Data.Results.Should().Be(1);
            result.Data.TotalPages.Should().Be(1);
            result.Data.TotalResults.Should().Be(1);
            result.Data.IsEmpty.Should().BeFalse();
        }

        [Test]
        public async Task get_deposit_should_return_deposit()
        {
            var depositId = TestItem.KeccakA;
            _consumerService.GetDepositAsync(depositId).Returns(GetDepositDetails());
            var result = await _rpc.ndm_getDeposit(depositId);
            await _consumerService.Received().GetDepositAsync(depositId);
            result.Data.Id.Should().Be(Keccak.OfAnEmptyString);
            result.Data.Deposit.Should().NotBeNull();
            result.Data.Deposit.Id.Should().Be(Keccak.OfAnEmptyString);
            result.Data.Deposit.Units.Should().Be(1);
            result.Data.Deposit.Value.Should().Be(1);
            result.Data.Deposit.ExpiryTime.Should().Be(1);
            result.Data.DataHeader.Should().NotBeNull();
            result.Data.DataHeader.Id.Should().NotBeNull();
            result.Data.DataHeader.Name.Should().NotBeNullOrWhiteSpace();
            result.Data.DataHeader.Description.Should().NotBeNullOrWhiteSpace();
            result.Data.DataHeader.UnitPrice.Should().Be(1);
            result.Data.DataHeader.UnitType.Should().Be(DataHeaderUnitType.Unit.ToString().ToLowerInvariant());
        }

        private static DataHeader GetDataHeader() => new DataHeader(Keccak.OfAnEmptyString, "test", "test", 1,
            DataHeaderUnitType.Unit, 0, 10, new DataHeaderRules(new DataHeaderRule(1)),
            new DataHeaderProvider(Address.Zero, "test"));

        private static DepositDetails GetDepositDetails()
            => new DepositDetails(new Deposit(Keccak.OfAnEmptyString, 1, 1, 1),
                GetDataHeader(), Array.Empty<byte>(), 1, TestItem.KeccakA);
    }
}