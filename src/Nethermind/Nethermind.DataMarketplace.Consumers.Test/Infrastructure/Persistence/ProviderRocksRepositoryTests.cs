// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rlp;
using Nethermind.Db;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Persistence
{
    [TestFixture]
    public class ProviderRocksRepositoryTests
    {
        public static IEnumerable<DepositDetails> TestCaseSource()
        {
            return DepositDetailsRocksRepositoryTests.TestCaseSource();
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public async Task Get_providers(DepositDetails depositDetails)
        {
            IDb db = new MemDb();
            IDepositUnitsCalculator depositUnitsCalculator = Substitute.For<IDepositUnitsCalculator>();
            DepositDetailsRocksRepository detailsRocksRepository = new DepositDetailsRocksRepository(db, new DepositDetailsDecoder(), depositUnitsCalculator);
            await detailsRocksRepository.AddAsync(depositDetails);

            ProviderRocksRepository repository = new ProviderRocksRepository(db, new DepositDetailsDecoder());

            var retrieved = await repository.GetProvidersAsync();
            retrieved.Count.Should().Be(1);
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public async Task Get_assets(DepositDetails depositDetails)
        {
            IDb db = new MemDb();
            IDepositUnitsCalculator depositUnitsCalculator = Substitute.For<IDepositUnitsCalculator>();
            DepositDetailsRocksRepository detailsRocksRepository = new DepositDetailsRocksRepository(db, new DepositDetailsDecoder(), depositUnitsCalculator);
            await detailsRocksRepository.AddAsync(depositDetails);

            ProviderRocksRepository repository = new ProviderRocksRepository(db, new DepositDetailsDecoder());

            var retrieved = await repository.GetDataAssetsAsync();
            retrieved.Count.Should().Be(1);
        }

        [Test]
        public async Task Get_providers_empty_db()
        {
            IDb db = new MemDb();
            IDepositUnitsCalculator depositUnitsCalculator = Substitute.For<IDepositUnitsCalculator>();
            DepositDetailsRocksRepository detailsRocksRepository = new DepositDetailsRocksRepository(db, new DepositDetailsDecoder(), depositUnitsCalculator);
            ProviderRocksRepository repository = new ProviderRocksRepository(db, new DepositDetailsDecoder());

            var retrieved = await repository.GetProvidersAsync();
            retrieved.Count.Should().Be(0);
        }

        [Test]
        public async Task Get_assets_empty_db()
        {
            IDb db = new MemDb();
            IDepositUnitsCalculator depositUnitsCalculator = Substitute.For<IDepositUnitsCalculator>();
            DepositDetailsRocksRepository detailsRocksRepository = new DepositDetailsRocksRepository(db, new DepositDetailsDecoder(), depositUnitsCalculator);
            ProviderRocksRepository repository = new ProviderRocksRepository(db, new DepositDetailsDecoder());

            var retrieved = await repository.GetDataAssetsAsync();
            retrieved.Count.Should().Be(0);
        }
    }
}
