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

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Services
{
    public class NdmFaucetTests
    {
        private INdmBlockchainBridge _blockchainBridge;
        private IEthRequestRepository _repository;
        private Address _faucetAddress;
        private UInt256 _maxValue;
        private UInt256 _dailyRequestsTotalValueEth;
        private bool _enabled;
        private ITimestamper _timestamper;
        private IWallet _wallet;
        private ILogManager _logManager;
        private INdmFaucet _faucet;
        private string _host;
        private Address _address;
        private UInt256 _value;
        private Account _faucetAccount;
        private Keccak _transactionHash;

        [SetUp]
        public void Setup()
        {
            _blockchainBridge = Substitute.For<INdmBlockchainBridge>();
            _repository = Substitute.For<IEthRequestRepository>();
            _repository.SumDailyRequestsTotalValueAsync(Arg.Any<DateTime>()).ReturnsForAnyArgs(UInt256.Zero);
            _faucetAddress = Address.FromNumber(1);
            _maxValue = 1.Ether();
            _dailyRequestsTotalValueEth = 500;
            _enabled = true;
            _timestamper = new Timestamper();
            _wallet = Substitute.For<IWallet>();
            _wallet.Sign(Arg.Any<Keccak>(), Arg.Any<Address>()).Returns(new Signature(new byte[65]));
            _logManager = LimboLogs.Instance;
            _host = "127.0.0.1";
            _address = Address.FromNumber(2);
            _value = 1.Ether();
            _faucetAccount = Account.TotallyEmpty;
            _transactionHash = Keccak.Zero;
            _blockchainBridge.GetNonceAsync(_faucetAddress).Returns(UInt256.Zero);
            _blockchainBridge.SendOwnTransactionAsync(Arg.Any<Transaction>()).Returns(_transactionHash);
        }

        [Test]
        public async Task request_eth_should_fail_for_disabled_faucet()
        {
            _enabled = false;
            await InitFaucetAsync();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().Be(FaucetResponse.FaucetDisabled);
        }

        [Test]
        public async Task request_eth_should_fail_for_null_faucet_address()
        {
            _faucetAddress = null;
            await InitFaucetAsync();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().Be(FaucetResponse.FaucetDisabled);
        }

        [Test]
        public async Task request_eth_should_fail_for_zero_faucet_address()
        {
            _faucetAddress = Address.Zero;
            await InitFaucetAsync();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().Be(FaucetResponse.FaucetAddressNotSet);
        }

        [Test]
        [Retry(3)]
        public async Task request_eth_should_fail_for_empty_host()
        {
            _host = string.Empty;
            await InitFaucetAsync();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().Be(FaucetResponse.InvalidNodeAddress);
        }

        [Test]
        public async Task request_eth_should_fail_for_null_address()
        {
            _address = null;
            await InitFaucetAsync();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().Be(FaucetResponse.InvalidNodeAddress);
        }

        [Test]
        public async Task request_eth_should_fail_for_zero_address()
        {
            _address = Address.Zero;
            await InitFaucetAsync();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().Be(FaucetResponse.InvalidNodeAddress);
        }

        [Test]
        public async Task request_eth_should_fail_for_faucet_address_equal_address()
        {
            _address = _faucetAddress;
            await InitFaucetAsync();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().Be(FaucetResponse.SameAddressAsFaucet);
        }
        
        private async Task WaitFor(Func<bool> isConditionMet, string description = "condition to be met")
        {
            const int waitInterval = 10;
            for (int i = 0; i < 10; i++)
            {
                if (isConditionMet())
                {
                    return;
                }

                TestContext.WriteLine($"({i}) Waiting {waitInterval} for {description}");
                await Task.Delay(waitInterval);
            }
        }

        [Test]
        public async Task request_eth_should_fail_for_zero_value()
        {
            _value = 0;
            await InitFaucetAsync();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().Be(FaucetResponse.ZeroValue);
        }

        [Test]
        public async Task request_eth_should_fail_for_value_bigger_than_max_value()
        {
            _value = _maxValue + 1;
            await InitFaucetAsync();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().Be(FaucetResponse.TooBigValue);
        }

        [Test]
        public async Task request_eth_should_fail_when_same_ip_address_sends_another_request_the_same_day()
        {
            var requestedAt = new DateTime(2019, 1, 1);
            _timestamper = new Timestamper(requestedAt);
            var latestRequest = new EthRequest(Keccak.Zero, _host, _address, _value, requestedAt, Keccak.Zero);
            _repository.GetLatestAsync(_host).Returns(latestRequest);
            await InitFaucetAsync();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should()
                .Be(FaucetResponse.RequestAlreadyProcessedToday(FaucetRequestDetails.From(latestRequest)));
        }

        [Test]
        public async Task request_eth_should_fail_when_today_requests_value_limit_was_reached()
        {
            var requestedAt = new DateTime(2019, 1, 1);
            _timestamper = new Timestamper(requestedAt);
            _dailyRequestsTotalValueEth = 3;
            await InitFaucetAsync();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Status.Should().Be(FaucetRequestStatus.RequestCompleted);
            ethRequested = await TryRequestEthAsync();
            ethRequested.Status.Should().Be(FaucetRequestStatus.RequestCompleted);
            ethRequested = await TryRequestEthAsync();
            ethRequested.Status.Should().Be(FaucetRequestStatus.RequestCompleted);
            ethRequested = await TryRequestEthAsync();
            ethRequested.Should().Be(FaucetResponse.DailyRequestsTotalValueReached);
        }

        [Test]
        public async Task request_eth_should_succeed_when_no_previous_requests_were_made()
        {
            _timestamper = new Timestamper(DateTime.UtcNow);
            await InitFaucetAsync();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().Be(FaucetResponse.RequestCompleted(new FaucetRequestDetails("127.0.0.1",
                _address, _value, _timestamper.UtcNow, Keccak.Zero)));
        }

        [Test]
        public async Task request_eth_should_succeed_when_previous_request_was_made_at_least_yesterday()
        {
            var requestedAt = new DateTime(2019, 1, 1);
            _timestamper = new Timestamper(requestedAt.AddDays(1));
            var latestRequest = new EthRequest(Keccak.Zero, _host, _address, _value, requestedAt, Keccak.Zero);
            _repository.GetLatestAsync(_host).Returns(latestRequest);
            await InitFaucetAsync();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().Be(FaucetResponse.RequestCompleted(new FaucetRequestDetails("127.0.0.1",
                _address, _value, _timestamper.UtcNow, Keccak.Zero)));
        }

        private async Task InitFaucetAsync()
        {
            _faucet = new NdmFaucet(_blockchainBridge, _repository, _faucetAddress, _maxValue,
                _dailyRequestsTotalValueEth, _enabled, _timestamper, _wallet, _logManager);
            await WaitFor(() => _faucet.IsInitialized);
        }

        private Task<FaucetResponse> TryRequestEthAsync() => _faucet.TryRequestEthAsync(_host, _address, _value);
    }
}
