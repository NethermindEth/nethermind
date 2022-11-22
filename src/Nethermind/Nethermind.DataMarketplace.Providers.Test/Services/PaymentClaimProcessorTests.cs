// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.DataMarketplace.Providers.Services;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Providers.Test.Services
{
    internal class PaymentClaimProcessorTests
    {
        private IGasPriceService _gasPriceService;
        private IConsumerRepository _consumerRepository;
        private IPaymentClaimRepository _paymentClaimRepository;
        private IPaymentClaimProcessor _processor;
        private IPaymentService _paymentService;
        private Address _coldWalletAddress;
        private IRlpObjectDecoder<Consumer> _consumerRlpDecoder;
        private IRlpObjectDecoder<UnitsRange> _unitsRangeRlpDecoder;
        private ITimestamper _timestamper;
        private Transaction _transaction;
        private UInt256 _gasPrice;

        [SetUp]
        public void Setup()
        {
            _gasPrice = 20.GWei();
            _gasPriceService = Substitute.For<IGasPriceService>();
            _consumerRepository = Substitute.For<IConsumerRepository>();
            _paymentClaimRepository = Substitute.For<IPaymentClaimRepository>();
            _paymentService = Substitute.For<IPaymentService>();
            _coldWalletAddress = Address.Zero;
            _consumerRlpDecoder = Substitute.For<IRlpObjectDecoder<Consumer>>();
            _consumerRlpDecoder.Encode(Arg.Any<Consumer>()).ReturnsForAnyArgs(new Rlp(Array.Empty<byte>()));
            _unitsRangeRlpDecoder = Substitute.For<IRlpObjectDecoder<UnitsRange>>();
            _unitsRangeRlpDecoder.Encode(Arg.Any<UnitsRange>()).ReturnsForAnyArgs(new Rlp(Array.Empty<byte>()));
            _timestamper = Substitute.For<ITimestamper>();
            _transaction = Build.A.Transaction.TestObject;
            _transaction.Hash = TestItem.KeccakA;
            _gasPriceService.GetCurrentPaymentClaimGasPriceAsync().Returns(_gasPrice);
            _processor = new PaymentClaimProcessor(_gasPriceService, _consumerRepository, _paymentClaimRepository,
                _paymentService, _coldWalletAddress, _timestamper, _unitsRangeRlpDecoder, LimboLogs.Instance);
        }

        [Test]
        public async Task given_receipt_request_range_0_to_19_payment_claim_range_should_be_0_to_19()
        {
            var unitsRange = new UnitsRange(0, 19);
            var consumer = CreateConsumer();
            _consumerRepository.GetAsync(Arg.Any<Keccak>()).Returns(consumer);
            _paymentService.ClaimPaymentAsync(Arg.Any<PaymentClaim>(), _coldWalletAddress, _gasPrice)
                .Returns(_transaction.Hash);
            var receiptRequest = new DataDeliveryReceiptRequest(1, Keccak.Zero, unitsRange);
            var paymentClaim = await _processor.ProcessAsync(receiptRequest, null);
            paymentClaim.ClaimedUnits.Should().Be(20);
            paymentClaim.UnitsRange.Should().Be(unitsRange);
        }


        [Test]
        public async Task given_valid_transaction_hash_returned_payment_claim_should_have_status_sent()
        {
            var unitsRange = new UnitsRange(0, 1);
            var consumer = CreateConsumer();
            _consumerRepository.GetAsync(Arg.Any<Keccak>()).Returns(consumer);
            _paymentService.ClaimPaymentAsync(Arg.Any<PaymentClaim>(), _coldWalletAddress, _gasPrice)
                .Returns(_transaction.Hash);
            var receiptRequest = new DataDeliveryReceiptRequest(1, Keccak.Zero, unitsRange);
            var paymentClaim = await _processor.ProcessAsync(receiptRequest, null);
            paymentClaim.Transaction.Hash.Should().Be(_transaction.Hash);
            paymentClaim.Status.Should().Be(PaymentClaimStatus.Sent);
        }

        [Test]
        public async Task given_null_transaction_hash_returned_payment_claim_should_have_status_unknown()
        {
            var unitsRange = new UnitsRange(0, 1);
            var consumer = CreateConsumer();
            _consumerRepository.GetAsync(Arg.Any<Keccak>()).Returns(consumer);
            var receiptRequest = new DataDeliveryReceiptRequest(1, Keccak.Zero, unitsRange);
            var paymentClaim = await _processor.ProcessAsync(receiptRequest, null);
            paymentClaim.Transaction.Should().BeNull();
            paymentClaim.Status.Should().Be(PaymentClaimStatus.Unknown);
        }

        [Test]
        public async Task payment_claim_should_not_be_sent_while_units_are_less_than_units_range_to()
        {
            var unitsRange = new UnitsRange(0, 5);
            var paymentClaim = new PaymentClaim(id: Keccak.Zero,
                                                depositId: Keccak.Zero,
                                                assetId: Keccak.Zero,
                                                assetName: "test",
                                                units: 3,
                                                claimedUnits: 3,
                                                unitsRange: unitsRange,
                                                value: 10,
                                                claimedValue: 30,
                                                expiryTime: 10,
                                                pepper: Array.Empty<byte>(),
                                                provider: TestItem.AddressA,
                                                consumer: TestItem.AddressB,
                                                signature: new Signature(1, 2, 37),
                                                timestamp: 12,
                                                transactions: Array.Empty<TransactionInfo>(),
                                                status: PaymentClaimStatus.Unknown
                                                );

            Keccak? transactionHash = await _processor.SendTransactionAsync(paymentClaim, 10);

            Assert.IsNull(transactionHash);
            Assert.IsTrue(paymentClaim.Status != PaymentClaimStatus.Sent);
        }

        private static Consumer CreateConsumer()
            => new Consumer(Keccak.Zero, 0,
                new DataRequest(Keccak.Zero, 10000, 10000, 10000, null, null, null, null),
                new DataAsset(Keccak.OfAnEmptyString, "test", "test", 1000, DataAssetUnitType.Unit, 0, 100000,
                    new DataAssetRules(new DataAssetRule(1000)), new DataAssetProvider(Address.Zero, "test")));
    }
}
