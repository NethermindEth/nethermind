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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Services
{
    public class NdmFaucetTests
    {
        private IBlockchainBridge _blockchainBridge;
        private IEthRequestRepository _repository;
        private Address _faucetAddress;
        private UInt256 _maxValue;
        private bool _enabled;
        private ITimestamp _timestamp;
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
            _blockchainBridge = Substitute.For<IBlockchainBridge>();
            _repository = Substitute.For<IEthRequestRepository>();
            _faucetAddress = Address.FromNumber(1);
            _maxValue = 1.GWei();
            _enabled = true;
            _timestamp = new Timestamp();
            _logManager = NullLogManager.Instance;
            _host = "127.0.0.1";
            _address = Address.FromNumber(2);
            _value = 1.GWei();
            _faucetAccount = Account.TotallyEmpty;
            _transactionHash = Keccak.Zero;
            _blockchainBridge.GetAccount(_faucetAddress).Returns(_faucetAccount);
            _blockchainBridge.SendTransaction(Arg.Any<Transaction>()).Returns(_transactionHash);
        }

        [Test]
        public async Task request_eth_should_fail_for_disabled_faucet()
        {
            _enabled = false;
            InitFaucet();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().BeFalse();
        }
        
        [Test]
        public async Task request_eth_should_fail_for_null_faucet_address()
        {
            _faucetAddress = null;
            InitFaucet();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().BeFalse();
        }
        
        [Test]
        public async Task request_eth_should_fail_for_zero_faucet_address()
        {
            _faucetAddress = Address.Zero;
            InitFaucet();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().BeFalse();
        }
        
        [Test]
        public async Task request_eth_should_fail_for_empty_host()
        {
            _host = string.Empty;
            InitFaucet();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().BeFalse();
        }
        
        [Test]
        public async Task request_eth_should_fail_for_null_address()
        {
            _address = null;
            InitFaucet();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().BeFalse();
        }
        
        [Test]
        public async Task request_eth_should_fail_for_zero_address()
        {
            _address = Address.Zero;
            InitFaucet();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().BeFalse();
        }
        
        [Test]
        public async Task request_eth_should_fail_for_faucet_address_equal_address()
        {
            _address = _faucetAddress;
            InitFaucet();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().BeFalse();
        }
        
        [Test]
        public async Task request_eth_should_fail_for_zero_value()
        {
            _value = 0;
            InitFaucet();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().BeFalse();
        }
        
        [Test]
        public async Task request_eth_should_fail_for_value_bigger_than_max_value()
        {
            _value = _maxValue + 1;
            InitFaucet();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().BeFalse();
        }
        
        [Test]
        public async Task request_eth_should_fail_when_same_ip_address_sends_another_request_the_same_day()
        {
            var requestedAt = new DateTime(2019, 1, 1);
            _timestamp = new Timestamp(requestedAt);
            var latestRequest = new EthRequest(Keccak.Zero, _host, _address, _value, requestedAt, Keccak.Zero);
            _repository.GetLatestAsync(_host).Returns(latestRequest);
            InitFaucet();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().BeFalse();
        }
        
        [Test]
        public async Task request_eth_should_succeed_when_no_previous_requests_were_made()
        {
            InitFaucet();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().BeTrue();
        }
        
        [Test]
        public async Task request_eth_should_succeed_when_previous_request_was_made_at_least_yesterday()
        {
            var requestedAt = new DateTime(2019, 1, 1);
            _timestamp = new Timestamp(requestedAt.AddDays(1));
            var latestRequest = new EthRequest(Keccak.Zero, _host, _address, _value, requestedAt, Keccak.Zero);
            _repository.GetLatestAsync(_host).Returns(latestRequest);
            InitFaucet();
            var ethRequested = await TryRequestEthAsync();
            ethRequested.Should().BeTrue();
        }

        private void InitFaucet()
            => _faucet = new NdmFaucet(_blockchainBridge, _repository, _faucetAddress, _maxValue, _enabled,
                _timestamp, _logManager);

        private Task<bool> TryRequestEthAsync() => _faucet.TryRequestEthAsync(_host, _address, _value);
    }
}