// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
