// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Deposits.Services;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Deposits
{
    [TestFixture]
    public class KycVerifierTests
    {
        private DataAsset _asset;
        private Address _providerAddress = TestItem.AddressA;
        private KycVerifier _kycVerifier;

        [SetUp]
        public void Setup()
        {
            DataAssetProvider provider = new DataAssetProvider(_providerAddress, "provider");
            _asset = new DataAsset(Keccak.Compute("1"), "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1)), provider, state: DataAssetState.Published);

            IConsumerDepositApprovalRepository repository = new ConsumerDepositApprovalInMemoryRepository();
            _kycVerifier = new KycVerifier(repository, LimboLogs.Instance);
        }

        [Test]
        public async Task _when_no_approval_returns_unverified()
        {
            var result = await _kycVerifier.IsVerifiedAsync(_asset.Id, TestItem.AddressA);
            result.Should().BeFalse();
        }
    }
}
