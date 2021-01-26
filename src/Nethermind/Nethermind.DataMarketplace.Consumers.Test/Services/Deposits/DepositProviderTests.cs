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
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Services;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Databases;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Deposits
{
    [TestFixture]
    public class DepositProviderTests
    {
        private Deposit _deposit;
        private DepositDetails _details;
        private DataAsset _asset;
        private Address _providerAddress = TestItem.AddressA;
        private DepositProvider _depositProvider;

        [SetUp]
        public void Setup()
        {
            DataAssetProvider provider = new DataAssetProvider(_providerAddress, "provider");
            IDepositUnitsCalculator depositUnitsCalculator = Substitute.For<IDepositUnitsCalculator>();
            _asset = new DataAsset(Keccak.Compute("1"), "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1)), provider, state: DataAssetState.Published);
            _deposit = new Deposit(Keccak.Zero, 1, 2, 3);
            _details = new DepositDetails(_deposit, _asset, Address.Zero, new byte[0], 1, new TransactionInfo[0]);

            DepositDetailsInMemoryRepository repository = new DepositDetailsInMemoryRepository(new DepositsInMemoryDb(), depositUnitsCalculator);
            repository.AddAsync(_details);

            ConsumerSessionInMemoryRepository sessionInMemoryRepository = new ConsumerSessionInMemoryRepository();
            _depositProvider = new DepositProvider(repository, new DepositUnitsCalculator(sessionInMemoryRepository, Timestamper.Default), LimboLogs.Instance);
        }

        [Test]
        public async Task Can_get_a_single_item_report()
        {
            var result = await _depositProvider.GetAsync(Keccak.Zero);
            result.Should().NotBeNull();
        }
    }
}
