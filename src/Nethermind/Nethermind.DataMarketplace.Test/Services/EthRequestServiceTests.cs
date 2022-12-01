// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            _logManager = LimboLogs.Instance; ;
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
