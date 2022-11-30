// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Queries;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Persistence
{
    [TestFixture]
    public class ConsumerSessionRocksRepositoryTests
    {
        static ConsumerSessionRocksRepositoryTests()
        {
            if (_cases == null)
            {
                _cases = new List<ConsumerSession>();
                _cases.Add(new ConsumerSession(
                    TestItem.KeccakA,
                    TestItem.KeccakB,
                    TestItem.KeccakC,
                    TestItem.AddressA,
                    TestItem.PublicKeyA,
                    TestItem.AddressB,
                    TestItem.PublicKeyB,
                    SessionState.Unknown,
                    1,
                    1,
                    2,
                    0,
                    1,
                    1,
                    1,
                    0,
                    1,
                    DataAvailability.Available
                ));

                _cases.Add(new ConsumerSession(
                    TestItem.KeccakB,
                    TestItem.KeccakB,
                    TestItem.KeccakC,
                    TestItem.AddressA,
                    TestItem.PublicKeyA,
                    TestItem.AddressC,
                    TestItem.PublicKeyB,
                    SessionState.Unknown,
                    0,
                    0,
                    startTimestamp: 3
                ));

                _cases.Add(new ConsumerSession(
                    TestItem.KeccakC,
                    TestItem.KeccakB,
                    TestItem.KeccakC,
                    TestItem.AddressA,
                    TestItem.PublicKeyA,
                    TestItem.AddressC,
                    TestItem.PublicKeyB,
                    SessionState.Unknown,
                    1,
                    1,
                    4,
                    0,
                    1,
                    1,
                    1,
                    0,
                    1,
                    DataAvailability.Available
                ));
            }
        }

        private static List<ConsumerSession> _cases;

        public static IEnumerable<ConsumerSession> TestCaseSource()
        {
            return _cases;
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public async Task Add_get(ConsumerSession session)
        {
            IDb db = new MemDb();
            ConsumerSessionRocksRepository repository = new ConsumerSessionRocksRepository(db, new ConsumerSessionDecoder());
            await repository.AddAsync(session);
            ConsumerSession retrieved = await repository.GetAsync(session.Id);
            retrieved.Should().BeEquivalentTo(session);
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public async Task Update_get(ConsumerSession session)
        {
            IDb db = new MemDb();
            ConsumerSessionRocksRepository repository = new ConsumerSessionRocksRepository(db, new ConsumerSessionDecoder());
            await repository.UpdateAsync(session);
            ConsumerSession retrieved = await repository.GetAsync(session.Id);
            retrieved.Should().BeEquivalentTo(session);
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public async Task Add_update_get(ConsumerSession session)
        {
            IDb db = new MemDb();
            ConsumerSessionRocksRepository repository = new ConsumerSessionRocksRepository(db, new ConsumerSessionDecoder());
            await repository.AddAsync(_cases[0]);
            await repository.UpdateAsync(session);
            ConsumerSession retrieved = await repository.GetAsync(session.Id);
            retrieved.Should().BeEquivalentTo(session);
        }

        [Test]
        public async Task Previous_of_first()
        {
            IDb db = new MemDb();
            ConsumerSessionRocksRepository repository = new ConsumerSessionRocksRepository(db, new ConsumerSessionDecoder());
            await repository.AddAsync(_cases[0]);
            ConsumerSession retrieved = await repository.GetPreviousAsync(_cases[0]);
            retrieved.Should().BeNull();
        }

        [Test]
        public async Task Previous_of_second()
        {
            IDb db = new MemDb();
            ConsumerSessionRocksRepository repository = new ConsumerSessionRocksRepository(db, new ConsumerSessionDecoder());
            await repository.AddAsync(_cases[1]);
            await repository.AddAsync(_cases[0]);
            ConsumerSession retrieved = await repository.GetPreviousAsync(_cases[1]);
            retrieved.Should().BeEquivalentTo(_cases[0]);
        }

        [Test]
        public void Null_query_throws()
        {
            IDb db = new MemDb();
            ConsumerSessionRocksRepository repository = new ConsumerSessionRocksRepository(db, new ConsumerSessionDecoder());
            Assert.Throws<ArgumentNullException>(() => repository.BrowseAsync(null));
        }

        [Test]
        public async Task Previous_of_non_persisted()
        {
            IDb db = new MemDb();
            ConsumerSessionRocksRepository repository = new ConsumerSessionRocksRepository(db, new ConsumerSessionDecoder());
            ConsumerSession retrieved = await repository.GetPreviousAsync(_cases[1]);
            retrieved.Should().BeNull();
        }

        [Test]
        public async Task Specific_browse()
        {
            IDb db = new MemDb();
            ConsumerSessionRocksRepository repository = new ConsumerSessionRocksRepository(db, new ConsumerSessionDecoder());
            await repository.AddAsync(_cases[1]);
            await repository.AddAsync(_cases[0]);

            GetConsumerSessions query = new GetConsumerSessions();
            query.ConsumerAddress = _cases[0].ConsumerAddress;
            query.DepositId = _cases[0].DepositId;
            query.ProviderAddress = _cases[0].ProviderAddress;
            query.ConsumerNodeId = _cases[0].ConsumerNodeId;
            query.DataAssetId = _cases[0].DataAssetId;
            query.ProviderNodeId = _cases[0].ProviderNodeId;

            PagedResult<ConsumerSession> retrieved = await repository.BrowseAsync(query);
            retrieved.Items.Should().ContainEquivalentOf(_cases[0]);
            retrieved.Items.Should().HaveCount(1);
        }
    }
}
