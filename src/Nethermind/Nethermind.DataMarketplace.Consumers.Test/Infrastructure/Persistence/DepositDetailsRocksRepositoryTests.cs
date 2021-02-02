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
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Db;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Persistence
{
    [TestFixture]
    public class DepositDetailsRocksRepositoryTests
    {
        private DepositDetailsRocksRepository repository;
        private IDepositUnitsCalculator depositUnitsCalculator;
        private static List<DepositDetails> _cases;

        public static IEnumerable<DepositDetails> TestCaseSource()
        {
            return _cases;
        }

        static DepositDetailsRocksRepositoryTests()
        {
            if (_cases == null)
            {
                DepositDecoder.Init();
                TransactionInfoDecoder.Init();
                DataAssetDecoder.Init();
                DataAssetRuleDecoder.Init();
                DataAssetRulesDecoder.Init();
                DataAssetProviderDecoder.Init();
                EarlyRefundTicketDecoder.Init();
                
                Deposit deposit = new Deposit(TestItem.KeccakA, 100, 100, 100);
                DataAssetProvider provider = new DataAssetProvider(TestItem.AddressA, "provider");
                DataAsset dataAsset = new DataAsset(TestItem.KeccakA, "data_asset", "desc", 1, DataAssetUnitType.Time, 1000, 10000, new DataAssetRules(new DataAssetRule(1), null), provider, null, QueryType.Stream, DataAssetState.Published, null, false, null);
                DepositDetails details = new DepositDetails(
                    deposit,
                    dataAsset,
                    TestItem.AddressA,
                    Array.Empty<byte>(),
                    10,
                    Array.Empty<TransactionInfo>(),
                    9,
                    false,
                    false,
                    null,
                    Array.Empty<TransactionInfo>(),
                    false,
                    false,
                    null,
                    0,
                    6);
                _cases = new List<DepositDetails>();
                _cases.Add(details);

                _cases.Add(new DepositDetails(
                    deposit,
                    dataAsset,
                    TestItem.AddressA,
                    Array.Empty<byte>(),
                    10,
                    Array.Empty<TransactionInfo>(),
                    9,
                    false,
                    false,
                    null,
                    Array.Empty<TransactionInfo>(),
                    false,
                    false,
                    null,
                    0,
                    6));
                
                _cases.Add(new DepositDetails(
                    deposit,
                    dataAsset,
                    TestItem.AddressA,
                    Array.Empty<byte>(),
                    10,
                    Array.Empty<TransactionInfo>(),
                    9,
                    false,
                    false,
                    null,
                    Array.Empty<TransactionInfo>(),
                    false,
                    false,
                    null,
                    0,
                    6));

                _cases.Add(new DepositDetails(
                    deposit,
                    dataAsset,
                    TestItem.AddressD,
                    Array.Empty<byte>(),
                    1000,
                    Array.Empty<TransactionInfo>()));
            }
        }

        [SetUp]
        public void SetUp()
        {
            IDb db = new MemDb();
            depositUnitsCalculator = Substitute.For<IDepositUnitsCalculator>();
            repository = new DepositDetailsRocksRepository(db, new DepositDetailsDecoder(), depositUnitsCalculator);
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public async Task Add_get(DepositDetails details)
        {
            await repository.AddAsync(details);
            DepositDetails retrieved = await repository.GetAsync(details.Id);
            retrieved.Should().BeEquivalentTo(details);
        }
        
        [Test]
        public async Task Get_null()
        {
            DepositDetails retrieved = await repository.GetAsync(Keccak.Zero);
            retrieved.Should().BeNull();
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public async Task Update_get(DepositDetails details)
        {
            await repository.UpdateAsync(details);
            DepositDetails retrieved = await repository.GetAsync(details.Id);
            retrieved.Should().BeEquivalentTo(details);
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public async Task Add_update_get(DepositDetails details)
        {
            await repository.AddAsync(_cases[0]);
            await repository.UpdateAsync(details);
            DepositDetails retrieved = await repository.GetAsync(details.Id);
            retrieved.Should().BeEquivalentTo(details);
        }

        [Test]
        public async Task Browse_by_eligible_to_refund()
        {
            depositUnitsCalculator.GetConsumedAsync(Arg.Is<DepositDetails>(d => d.Timestamp == 1000)).Returns(Task.FromResult((uint) 200));
            foreach (DepositDetails details in _cases)
            {
                await repository.AddAsync(details);
            }

            PagedResult<DepositDetails> result = await repository.BrowseAsync(new GetDeposits {EligibleToRefund = true, CurrentBlockTimestamp = 200});
            result.Items.Should().HaveCount(1);
        }

        [Test]
        public async Task eligable_to_refund_will_not_return_when_deposit_is_consumed()
        {
            var deposit = new Deposit(Keccak.Zero, units: 100, expiryTime: 100, value: 10);   

            var dataAsset = new DataAsset(Keccak.OfAnEmptyString, 
                                        name: "TestAsset", 
                                        description: "Test", 
                                        unitPrice: 10, 
                                        unitType: DataAssetUnitType.Unit,
                                        minUnits: 1,
                                        maxUnits: 100,
                                        rules: new DataAssetRules(new DataAssetRule(1)),
                                        provider: new DataAssetProvider(TestItem.AddressA, "provider"));

            var depositDetails = new DepositDetails(deposit,
                                                    dataAsset, 
                                                    consumer: TestItem.AddressB,
                                                    pepper: Array.Empty<byte>(),
                                                    timestamp: 50,
                                                    transactions: Array.Empty<TransactionInfo>());

            await repository.AddAsync(depositDetails);

            depositUnitsCalculator.GetConsumedAsync(depositDetails).Returns(Task.FromResult((uint)100));

            PagedResult<DepositDetails> result = await repository.BrowseAsync(new GetDeposits { EligibleToRefund = true });
            Assert.IsTrue(result.Items.Count == 0);
        }

        [Test]
        public async Task eligable_to_refund_will_return_when_deposit_is_not_consumed()
        {
            var deposit = new Deposit(Keccak.Zero, units: 100, expiryTime: 100, value: 10);   

            var dataAsset = new DataAsset(Keccak.OfAnEmptyString, 
                                        name: "TestAsset", 
                                        description: "Test", 
                                        unitPrice: 10, 
                                        unitType: DataAssetUnitType.Unit,
                                        minUnits: 1,
                                        maxUnits: 100,
                                        rules: new DataAssetRules(new DataAssetRule(1)),
                                        provider: new DataAssetProvider(TestItem.AddressA, "provider"));

            var depositDetails = new DepositDetails(deposit,
                                                    dataAsset, 
                                                    consumer: TestItem.AddressB,
                                                    pepper: Array.Empty<byte>(),
                                                    timestamp: 50,
                                                    transactions: Array.Empty<TransactionInfo>());

            await repository.AddAsync(depositDetails);

            depositUnitsCalculator.GetConsumedAsync(depositDetails).Returns(Task.FromResult((uint)99));

            PagedResult<DepositDetails> result = await repository.BrowseAsync(new GetDeposits { EligibleToRefund = true });
            Assert.IsTrue(result.Items.Count == 0);
        }
        
        [Test]
        public async Task Browse_pending_only()
        {
            foreach (DepositDetails details in _cases)
            {
                await repository.AddAsync(details);
            }

            PagedResult<DepositDetails> result = await repository.BrowseAsync(new GetDeposits {OnlyPending = true});
            result.Items.Should().HaveCount(0);
        }
        
        [Test]
        public async Task Browse_not_rejected_only()
        {
            foreach (DepositDetails details in _cases)
            {
                await repository.AddAsync(details);
            }

            PagedResult<DepositDetails> result = await repository.BrowseAsync(new GetDeposits {OnlyNotRejected = true});
            result.Items.Should().HaveCount(1);
        }
        
        [Test]
        public async Task Browse_unconfirmed_only()
        {
            foreach (DepositDetails details in _cases)
            {
                await repository.AddAsync(details);
            }

            PagedResult<DepositDetails> result = await repository.BrowseAsync(new GetDeposits {OnlyUnconfirmed = true});
            result.Items.Should().HaveCount(01);
        }

        [Test]
        public async Task Browse_empty()
        {
            foreach (DepositDetails details in _cases)
            {
                await repository.AddAsync(details);
            }

            PagedResult<DepositDetails> result = await repository.BrowseAsync(new GetDeposits
            {
                EligibleToRefund = true
            });
            result.Items.Should().HaveCount(0);
        }

        [Test]
        public async Task Browse_empty_database()
        {
            PagedResult<DepositDetails> result = await repository.BrowseAsync(new GetDeposits());
            result.Items.Should().HaveCount(0);
        }
        
        [Test]
        public void Null_query_throws()
        {
            Assert.ThrowsAsync<ArgumentNullException>(() => repository.BrowseAsync(null));
        }
    }
}
