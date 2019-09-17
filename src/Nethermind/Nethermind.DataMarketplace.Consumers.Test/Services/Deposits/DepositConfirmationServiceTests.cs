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
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Deposits.Services;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Deposits
{
    public class DepositConfirmationServiceTests
    {
        private IDepositConfirmationService _depositConfirmationService;
        private INdmBlockchainBridge _blockchainBridge;
        private IConsumerNotifier _consumerNotifier;
        private IDepositDetailsRepository _depositRepository;
        private IDepositService _depositService;
        private uint _requiredBlockConfirmations;

        [SetUp]
        public void Setup()
        {
            _blockchainBridge = Substitute.For<INdmBlockchainBridge>();
            _consumerNotifier = Substitute.For<IConsumerNotifier>();
            _depositRepository = Substitute.For<IDepositDetailsRepository>();
            _depositService = Substitute.For<IDepositService>();
            _requiredBlockConfirmations = 1;
            _depositConfirmationService = new DepositConfirmationService(_blockchainBridge, _consumerNotifier,
                _depositRepository, _depositService, LimboLogs.Instance, _requiredBlockConfirmations);
        }

        [Test]
        public async Task try_confirm_should_not_confirm_deposit_if_it_is_already_confirmed()
        {
            var deposit = GetDepositDetails();
            deposit.SetConfirmations(1);
            deposit.SetConfirmationTimestamp(1);
            deposit.SetTransactionHash(TestItem.KeccakA);
            await _depositConfirmationService.TryConfirmAsync(deposit);
            await _blockchainBridge.DidNotReceive().GetTransactionAsync(deposit.TransactionHash);
            deposit.Confirmed.Should().BeTrue();
        }
        
        [Test]
        public async Task try_confirm_should_not_confirm_deposit_if_it_is_already_rejected()
        {
            var deposit = GetDepositDetails();
            deposit.Reject();
            await _depositConfirmationService.TryConfirmAsync(deposit);
            await _blockchainBridge.DidNotReceive().GetTransactionAsync(deposit.TransactionHash);
            deposit.Rejected.Should().BeTrue();
        }
        
        [Test]
        public async Task try_confirm_should_not_confirm_deposit_if_transaction_was_not_found()
        {
            var deposit = GetDepositDetails();
            await _depositConfirmationService.TryConfirmAsync(deposit);
            await _blockchainBridge.Received().GetTransactionAsync(deposit.TransactionHash);
            await _blockchainBridge.DidNotReceive().GetLatestBlockNumberAsync();
            deposit.Confirmed.Should().BeFalse();
        }
        
        private static DepositDetails GetDepositDetails(uint timestamp = 0)
            => new DepositDetails(new Deposit(Keccak.Zero, 1, 1, 1),
                GetDataAsset(DataAssetUnitType.Unit), TestItem.AddressB, Array.Empty<byte>(), 1, TestItem.KeccakA,
                timestamp);

        private static DataAsset GetDataAsset(DataAssetUnitType unitType)
            => new DataAsset(Keccak.OfAnEmptyString, "test", "test", 1,
                unitType, 0, 10, new DataAssetRules(new DataAssetRule(1)),
                new DataAssetProvider(Address.Zero, "test"));
    }
}