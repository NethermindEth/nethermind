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
using Nethermind.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Int256;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Services
{
    public class EthRequestServiceTests
    {
        private const string FaucetHost = "127.0.0.1";
        private ILogManager _logManager;
        private INdmPeer _faucetPeer;
        private IEthRequestService _ethRequestService;

        [SetUp]
        public void Setup()
        {
            _logManager = LimboLogs.Instance;;
            _faucetPeer = Substitute.For<INdmPeer>();
            _ethRequestService = new EthRequestService(FaucetHost, _logManager);
        }

        [Test]
        public void faucet_host_should_return_valid_address()
        {
            _ethRequestService.FaucetHost.Should().Be(FaucetHost);
        }

        [Test]
        public async Task request_eth_should_fail_for_missing_faucet_peer()
        {
            var ethRequested = await _ethRequestService.TryRequestEthAsync(Address.Zero, 1);
            ethRequested.Should().Be(FaucetResponse.FaucetNotSet);
        }

        [Test]
        public async Task request_eth_should_fail_for_null_address()
        {
            _ethRequestService.UpdateFaucet(_faucetPeer);
            var ethRequested = await _ethRequestService.TryRequestEthAsync(null, 1);
            ethRequested.Should().Be(FaucetResponse.InvalidNodeAddress);
        }

        [Test]
        public async Task request_eth_should_fail_for_zero_address()
        {
            _ethRequestService.UpdateFaucet(_faucetPeer);
            var ethRequested = await _ethRequestService.TryRequestEthAsync(Address.Zero, 1);
            ethRequested.Should().Be(FaucetResponse.InvalidNodeAddress);
        }

        [Test]
        public async Task request_eth_should_fail_for_zero_value()
        {
            _ethRequestService.UpdateFaucet(_faucetPeer);
            var ethRequested = await _ethRequestService.TryRequestEthAsync(Address.FromNumber(1), 0);
            ethRequested.Should().Be(FaucetResponse.InvalidNodeAddress);
        }

        [Test]
        public async Task request_eth_should_succeed_for_valid_address_and_non_zero_value()
        {
            var address = Address.FromNumber(1);
            UInt256 value = 1;
            _ethRequestService.UpdateFaucet(_faucetPeer);
            _faucetPeer.SendRequestEthAsync(address, value)
                .Returns(FaucetResponse.RequestCompleted(FaucetRequestDetails.Empty));
            var ethRequested = await _ethRequestService.TryRequestEthAsync(Address.FromNumber(1), 1);
            ethRequested.Should().Be(FaucetResponse.RequestCompleted(FaucetRequestDetails.Empty));
        }
    }
}