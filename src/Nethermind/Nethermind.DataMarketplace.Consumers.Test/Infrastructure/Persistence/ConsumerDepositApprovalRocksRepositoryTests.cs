// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Persistence
{
    [TestFixture]
    public class ConsumerDepositApprovalRocksRepositoryTests
    {
        static ConsumerDepositApprovalRocksRepositoryTests()
        {
            if (_cases == null)
            {
                _cases = new List<DepositApproval>();
                _cases.Add(new DepositApproval(
                    TestItem.KeccakB,
                    "asset_name",
                    "kyc", TestItem.AddressA,
                    TestItem.AddressB,
                    1,
                    DepositApprovalState.Rejected));

                _cases.Add(new DepositApproval(
                    TestItem.KeccakD,
                    "asset_name",
                    "kyc",
                    TestItem.AddressC,
                    TestItem.AddressD,
                    2,
                    DepositApprovalState.Confirmed));

                _cases.Add(new DepositApproval(
                    TestItem.KeccakD,
                    "asset_name",
                    "kyc",
                    TestItem.AddressC,
                    TestItem.AddressD,
                    3,
                    DepositApprovalState.Pending));
            }
        }

        private static List<DepositApproval> _cases;

        public static IEnumerable<DepositApproval> TestCaseSource()
        {
            return _cases;
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public async Task Add_get(DepositApproval approval)
        {
            IDb db = new MemDb();
            ConsumerDepositApprovalRocksRepository repository = new ConsumerDepositApprovalRocksRepository(db, new DepositApprovalDecoder());
            await repository.AddAsync(approval);
            DepositApproval retrieved = await repository.GetAsync(approval.Id);
            retrieved.Should().BeEquivalentTo(approval);
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public async Task Update_get(DepositApproval approval)
        {
            IDb db = new MemDb();
            ConsumerDepositApprovalRocksRepository repository = new ConsumerDepositApprovalRocksRepository(db, new DepositApprovalDecoder());
            await repository.UpdateAsync(approval);
            DepositApproval retrieved = await repository.GetAsync(approval.Id);
            retrieved.Should().BeEquivalentTo(approval);
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public async Task Add_update_get(DepositApproval approval)
        {
            IDb db = new MemDb();
            ConsumerDepositApprovalRocksRepository repository = new ConsumerDepositApprovalRocksRepository(db, new DepositApprovalDecoder());
            await repository.AddAsync(_cases[0]);
            await repository.UpdateAsync(approval);
            DepositApproval retrieved = await repository.GetAsync(approval.Id);
            retrieved.Should().BeEquivalentTo(approval);
        }

        [Test]
        public async Task Browse_by_asset()
        {
            IDb db = new MemDb();
            ConsumerDepositApprovalRocksRepository repository = new ConsumerDepositApprovalRocksRepository(db, new DepositApprovalDecoder());
            foreach (DepositApproval approval in _cases)
            {
                await repository.AddAsync(approval);
            }

            PagedResult<DepositApproval> depositApprovals = await repository.BrowseAsync(new GetConsumerDepositApprovals { DataAssetId = TestItem.KeccakB });
            depositApprovals.Items.Should().ContainSingle(da => da.AssetId == TestItem.KeccakB);
        }

        [Test]
        public async Task Browse_by_provider()
        {
            IDb db = new MemDb();
            ConsumerDepositApprovalRocksRepository repository = new ConsumerDepositApprovalRocksRepository(db, new DepositApprovalDecoder());
            foreach (DepositApproval approval in _cases)
            {
                await repository.AddAsync(approval);
            }

            PagedResult<DepositApproval> depositApprovals = await repository.BrowseAsync(new GetConsumerDepositApprovals { Provider = TestItem.AddressB });
            depositApprovals.Items.Should().ContainSingle(da => da.Provider == TestItem.AddressB);
        }

        [Test]
        public async Task Browse_pending_only()
        {
            IDb db = new MemDb();
            ConsumerDepositApprovalRocksRepository repository = new ConsumerDepositApprovalRocksRepository(db, new DepositApprovalDecoder());
            foreach (DepositApproval approval in _cases)
            {
                await repository.AddAsync(approval);
            }

            PagedResult<DepositApproval> depositApprovals = await repository.BrowseAsync(new GetConsumerDepositApprovals { OnlyPending = true });
            depositApprovals.Items.Should().ContainSingle(da => da.State == DepositApprovalState.Pending);
        }

        [Test]
        public void Null_query_throws()
        {
            IDb db = new MemDb();
            ConsumerDepositApprovalRocksRepository repository = new ConsumerDepositApprovalRocksRepository(db, new DepositApprovalDecoder());
            Assert.Throws<ArgumentNullException>(() => repository.BrowseAsync(null));
        }

        [Test]
        public async Task Browse_empty()
        {
            IDb db = new MemDb();
            ConsumerDepositApprovalRocksRepository repository = new ConsumerDepositApprovalRocksRepository(db, new DepositApprovalDecoder());
            foreach (DepositApproval approval in _cases)
            {
                await repository.AddAsync(approval);
            }

            PagedResult<DepositApproval> depositApprovals = await repository.BrowseAsync(new GetConsumerDepositApprovals { DataAssetId = Keccak.Zero });
            depositApprovals.Items.Should().HaveCount(0);
        }

        [Test]
        public async Task Browse_empty_database()
        {
            IDb db = new MemDb();
            ConsumerDepositApprovalRocksRepository repository = new ConsumerDepositApprovalRocksRepository(db, new DepositApprovalDecoder());
            PagedResult<DepositApproval> depositApprovals = await repository.BrowseAsync(new GetConsumerDepositApprovals());
            depositApprovals.Items.Should().HaveCount(0);
        }
    }
}
