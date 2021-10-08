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
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Refunds;
using Nethermind.DataMarketplace.Consumers.Refunds.Services;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Test;
using Nethermind.Int256;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Refunds
{
    public class RefundClaimantTests : ContractInteractionTest
    {
        private IRefundService _refundService;
        private IDepositDetailsRepository _depositRepository;
        private ITransactionVerifier _transactionVerifier;
        private IRefundClaimant _refundClaimant;
        private IGasPriceService _gasPriceService;
        private ITimestamper _timestamper;
        private Keccak _claimedRefundTransactionHash;

        [SetUp]
        public void Setup()
        {
            Prepare();
            _claimedRefundTransactionHash = TestItem.KeccakA;
            _refundService = Substitute.For<IRefundService>();
            _refundService.ClaimRefundAsync(Arg.Any<Address>(), Arg.Any<RefundClaim>(), Arg.Any<UInt256>())
                .Returns(_claimedRefundTransactionHash);
            _refundService.ClaimEarlyRefundAsync(Arg.Any<Address>(), Arg.Any<EarlyRefundClaim>(), Arg.Any<UInt256>())
                .Returns(_claimedRefundTransactionHash);
            _depositRepository = Substitute.For<IDepositDetailsRepository>();
            _transactionVerifier = Substitute.For<ITransactionVerifier>();
            _transactionVerifier.VerifyAsync(Arg.Any<NdmTransaction>())
                .Returns(new TransactionVerifierResult(true, 1, 1));
            _gasPriceService = Substitute.For<IGasPriceService>();
            _timestamper = Timestamper.Default;
            _refundClaimant = new RefundClaimant(_refundService, _ndmBridge, _depositRepository,
                _transactionVerifier, _gasPriceService, _timestamper, LimboLogs.Instance);
        }

        [Test]
        public async Task refund_should_not_be_claimed_for_not_confirmed_deposit()
        {
            var deposit = GetDepositDetails();
            var refundTo = TestItem.AddressB;
            await _refundClaimant.TryClaimRefundAsync(deposit, refundTo);
            deposit.ClaimedRefundTransaction.Should().BeNull();
            await _refundService.DidNotReceiveWithAnyArgs().ClaimRefundAsync(null, null, Arg.Any<UInt256>());
            deposit.RefundClaimed.Should().BeFalse();
        }

        [Test]
        public async Task refund_should_not_be_claimed_for_rejected_deposit()
        {
            var deposit = GetDepositDetails();
            deposit.Reject();
            var refundTo = TestItem.AddressB;
            await _refundClaimant.TryClaimRefundAsync(deposit, refundTo);
            deposit.ClaimedRefundTransaction.Should().BeNull();
            await _refundService.DidNotReceiveWithAnyArgs().ClaimRefundAsync(null, null, Arg.Any<UInt256>());
            deposit.RefundClaimed.Should().BeFalse();
        }

        [Test]
        public async Task refund_should_be_claimed_for_confirmed_deposit()
        {
            var deposit = GetDepositDetails();
            deposit.SetConfirmations(1);
            deposit.SetConfirmationTimestamp(1);
            var refundTo = TestItem.AddressB;
            await _refundClaimant.TryClaimRefundAsync(deposit, refundTo);
            deposit.ClaimedRefundTransaction.Hash.Should().Be(_claimedRefundTransactionHash);
            await _refundService.Received(1).ClaimRefundAsync(refundTo, Arg.Any<RefundClaim>(), Arg.Any<UInt256>());
            deposit.RefundClaimed.Should().BeTrue();
        }

        [Test]
        public async Task claiming_refunds_multiple_times_for_same_deposit_should_not_send_new_transactions()
        {
            var deposit = GetDepositDetails();
            deposit.SetConfirmations(1);
            deposit.SetConfirmationTimestamp(1);
            var refundTo = TestItem.AddressB;
            for (var i = 0; i < 10; i++)
            {
                await _refundClaimant.TryClaimRefundAsync(deposit, refundTo);
            }

            deposit.ClaimedRefundTransaction.Hash.Should().Be(_claimedRefundTransactionHash);
            await _refundService.Received(1).ClaimRefundAsync(refundTo, Arg.Any<RefundClaim>(), Arg.Any<UInt256>());
            deposit.RefundClaimed.Should().BeTrue();
        }

        [Test]
        public async Task early_refund_should_not_be_claimed_for_not_confirmed_deposit()
        {
            var deposit = GetDepositDetails();
            var refundTo = TestItem.AddressB;
            await _refundClaimant.TryClaimEarlyRefundAsync(deposit, refundTo);
            deposit.ClaimedRefundTransaction.Should().BeNull();
            await _refundService.DidNotReceiveWithAnyArgs().ClaimEarlyRefundAsync(null, null, Arg.Any<UInt256>());
            deposit.RefundClaimed.Should().BeFalse();
        }

        [Test]
        public async Task early_refund_should_not_be_claimed_for_rejected_deposit()
        {
            var deposit = GetDepositDetails();
            deposit.Reject();
            var refundTo = TestItem.AddressB;
            await _refundClaimant.TryClaimEarlyRefundAsync(deposit, refundTo);
            deposit.ClaimedRefundTransaction.Should().BeNull();
            await _refundService.DidNotReceiveWithAnyArgs().ClaimEarlyRefundAsync(null, null, Arg.Any<UInt256>());
            deposit.RefundClaimed.Should().BeFalse();
        }

        [Test]
        public async Task early_refund_should_be_claimed_for_confirmed_deposit()
        {
            var deposit = GetDepositDetails();
            deposit.SetConfirmations(1);
            deposit.SetConfirmationTimestamp(1);
            var refundTo = TestItem.AddressB;
            await _refundClaimant.TryClaimEarlyRefundAsync(deposit, refundTo);
            deposit.ClaimedRefundTransaction.Hash.Should().Be(_claimedRefundTransactionHash);
            await _refundService.Received(1).ClaimEarlyRefundAsync(refundTo, Arg.Any<EarlyRefundClaim>(), Arg.Any<UInt256>());
            deposit.RefundClaimed.Should().BeTrue();
        }

        [Test]
        public async Task claiming_early_refunds_for_same_deposit_multiple_times_should_not_send_new_transactions()
        {
            var deposit = GetDepositDetails();
            deposit.SetConfirmations(1);
            deposit.SetConfirmationTimestamp(1);
            var refundTo = TestItem.AddressB;
            for (var i = 0; i < 10; i++)
            {
                await _refundClaimant.TryClaimEarlyRefundAsync(deposit, refundTo);
            }

            deposit.ClaimedRefundTransaction.Hash.Should().Be(_claimedRefundTransactionHash);
            await _refundService.Received(1).ClaimEarlyRefundAsync(refundTo, Arg.Any<EarlyRefundClaim>(), Arg.Any<UInt256>());
            deposit.RefundClaimed.Should().BeTrue();
        }

        private static DepositDetails GetDepositDetails()
            => new DepositDetails(new Deposit(Keccak.OfAnEmptyString, 1, 1, 1),
                GetDataAsset(), TestItem.AddressB, Array.Empty<byte>(), 1,
                new []{TransactionInfo.Default(TestItem.KeccakA, 1, 1, 1, 1)},
                earlyRefundTicket: new EarlyRefundTicket(Keccak.OfAnEmptyString, 1, null));

        private static DataAsset GetDataAsset()
            => new DataAsset(Keccak.OfAnEmptyString, "test", "test", 1,
                DataAssetUnitType.Unit, 0, 10, new DataAssetRules(new DataAssetRule(1)),
                new DataAssetProvider(Address.Zero, "test"));
    }
}