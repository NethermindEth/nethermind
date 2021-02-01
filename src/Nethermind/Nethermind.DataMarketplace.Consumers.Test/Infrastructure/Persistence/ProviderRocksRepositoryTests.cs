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
