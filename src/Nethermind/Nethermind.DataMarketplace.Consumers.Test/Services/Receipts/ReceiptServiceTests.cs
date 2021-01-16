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
using System.Diagnostics;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Providers;
using Nethermind.DataMarketplace.Consumers.Receipts;
using Nethermind.DataMarketplace.Consumers.Receipts.Services;
using Nethermind.DataMarketplace.Consumers.Sessions;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Receipts
{
    public class ReceiptServiceTests
    {
        private IReceiptService _receiptService;
        private IDepositProvider _depositProvider;
        private IProviderService _providerService;
        private IReceiptRequestValidator _receiptRequestValidator;
        private ISessionService _sessionService;
        private ITimestamper _timestamper;
        private IReceiptRepository _receiptRepository;
        private IConsumerSessionRepository _sessionRepository;
        private IAbiEncoder _abiEncoder;
        private IWallet _wallet;
        private IEthereumEcdsa _ecdsa;
        private PublicKey _nodePublicKey;

        [SetUp]
        public void Setup()
        {
            _depositProvider = Substitute.For<IDepositProvider>();
            _providerService = Substitute.For<IProviderService>();
            _receiptRequestValidator = Substitute.For<IReceiptRequestValidator>();
            _sessionService = Substitute.For<ISessionService>();
            _receiptRepository = Substitute.For<IReceiptRepository>();
            _sessionRepository = Substitute.For<IConsumerSessionRepository>();
            _abiEncoder = Substitute.For<IAbiEncoder>();
            _wallet = Substitute.For<IWallet>();
            _ecdsa = Substitute.For<IEthereumEcdsa>();
            _timestamper = Timestamper.Default;
            _nodePublicKey = TestItem.PublicKeyA;
            _receiptService = new ReceiptService(_depositProvider, _providerService, _receiptRequestValidator,
                _sessionService, _timestamper, _receiptRepository, _sessionRepository, _abiEncoder, _wallet,
                _ecdsa, _nodePublicKey, LimboLogs.Instance);
        }

        [Test]
        public async Task send_should_fail_if_deposit_does_not_exist()
        {
            var receipt = GetDataDeliveryReceiptRequest();
            await _receiptService.SendAsync(receipt);
            await _depositProvider.Received().GetAsync(receipt.DepositId);
            _sessionService.DidNotReceive().GetActive(receipt.DepositId);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(3)]
        public async Task send_should_fail_if_active_session_does_not_exist(int fetchSessionRetries)
        {
            const int fetchSessionRetryDelayMilliseconds = 100;
            var receipt = GetDataDeliveryReceiptRequest();
            var deposit = GetDepositDetails();
            _depositProvider.GetAsync(receipt.DepositId).Returns(deposit);
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            await _receiptService.SendAsync(receipt, fetchSessionRetries, fetchSessionRetryDelayMilliseconds);
            stopwatch.Stop();
            var expectedTime = 0.9 * fetchSessionRetryDelayMilliseconds * fetchSessionRetries;
            stopwatch.ElapsedMilliseconds.Should().BeGreaterOrEqualTo((long)expectedTime);
            await _depositProvider.Received().GetAsync(receipt.DepositId);
            _sessionService.Received(fetchSessionRetries + 1).GetActive(receipt.DepositId);
        }

        [Test]
        public async Task send_should_fail_if_provider_does_not_exist()
        {
            var receipt = GetDataDeliveryReceiptRequest();
            var deposit = GetDepositDetails();
            var session = GetConsumerSession();
            _depositProvider.GetAsync(receipt.DepositId).Returns(deposit);
            _sessionService.GetActive(receipt.DepositId).Returns(session);
            _providerService.GetPeer(deposit.DataAsset.Provider.Address).Returns((INdmPeer) null);
            await _receiptService.SendAsync(receipt, 0, 0);
            _providerService.Received().GetPeer(deposit.DataAsset.Provider.Address);
            _receiptRequestValidator.DidNotReceive().IsValid(receipt, session.UnpaidUnits, session.ConsumedUnits,
                deposit.Deposit.Units);
        }

        [Test]
        public async Task send_should_fail_if_validation_fails()
        {
            var seconds = _timestamper.UnixTime.Seconds;
            var receipt = GetDataDeliveryReceiptRequest();
            var deposit = GetDepositDetails();
            var session = GetConsumerSession();
            var receiptId = Keccak.Compute(Rlp.Encode(Rlp.Encode(receipt.DepositId), Rlp.Encode(receipt.Number),
                Rlp.Encode(seconds)).Bytes);
            var provider = Substitute.For<INdmPeer>();
            _depositProvider.GetAsync(receipt.DepositId).Returns(deposit);
            _sessionService.GetActive(receipt.DepositId).Returns(session);
            _providerService.GetPeer(deposit.DataAsset.Provider.Address).Returns(provider);
            await _receiptService.SendAsync(receipt, 0, 0);
            _providerService.Received().GetPeer(deposit.DataAsset.Provider.Address);
            _receiptRequestValidator.Received().IsValid(receipt, session.UnpaidUnits, session.ConsumedUnits,
                deposit.Deposit.Units);
            await _receiptRepository.Received().AddAsync(Arg.Is<DataDeliveryReceiptDetails>(x =>
                x.Id == receiptId &&
                x.SessionId == session.Id && 
                x.DataAssetId == session.DataAssetId &&
                x.ConsumerNodeId == _nodePublicKey &&
                x.Request.Equals(receipt) && 
                x.Timestamp == seconds && 
                !x.IsClaimed));
            await _sessionRepository.Received().UpdateAsync(session);
            provider.Received().SendDataDeliveryReceipt(receipt.DepositId, Arg.Is<DataDeliveryReceipt>(x =>
                x.StatusCode == StatusCodes.InvalidReceiptRequestRange &&
                x.ConsumedUnits == session.ConsumedUnits &&
                x.UnpaidUnits == session.UnpaidUnits &&
                x.Signature.Equals(new Signature(1, 1, 27))));

        }
        
        [Test]
        public async Task send_should_fail_if_recover_public_key_fails()
        {
            var timestamp = _timestamper.UnixTime.Seconds;
            var receipt = GetDataDeliveryReceiptRequest();
            var deposit = GetDepositDetails();
            var session = GetConsumerSession();
            var provider = Substitute.For<INdmPeer>();
            var receiptId = Keccak.Compute(Rlp.Encode(Rlp.Encode(receipt.DepositId), Rlp.Encode(receipt.Number),
                Rlp.Encode(timestamp)).Bytes);
            _depositProvider.GetAsync(receipt.DepositId).Returns(deposit);
            _sessionService.GetActive(receipt.DepositId).Returns(session);
            _providerService.GetPeer(deposit.DataAsset.Provider.Address).Returns(provider);
            _receiptRequestValidator.IsValid(receipt, session.UnpaidUnits, session.ConsumedUnits,
                deposit.Deposit.Units).Returns(true);
            await _receiptService.SendAsync(receipt, 0, 0);
            _abiEncoder.Received().Encode(AbiEncodingStyle.Packed, Arg.Any<AbiSignature>(),
                deposit.Id.Bytes,
                Arg.Is<uint[]>(x => x[0] == receipt.UnitsRange.From && x[1] == receipt.UnitsRange.To));
            _wallet.Received().Sign(Arg.Any<Keccak>(), deposit.Consumer);
            _ecdsa.Received().RecoverPublicKey(Arg.Any<Signature>(), Arg.Any<Keccak>());
            await _receiptRepository.Received().AddAsync(Arg.Is<DataDeliveryReceiptDetails>(x =>
                x.Id == receiptId &&
                x.SessionId == session.Id && 
                x.DataAssetId == session.DataAssetId &&
                x.ConsumerNodeId == _nodePublicKey &&
                x.Request.Equals(receipt) && 
                x.Timestamp == timestamp && 
                !x.IsClaimed));
            await _sessionRepository.Received().UpdateAsync(session);
            provider.Received().SendDataDeliveryReceipt(receipt.DepositId, Arg.Is<DataDeliveryReceipt>(x =>
                x.StatusCode == StatusCodes.InvalidReceiptAddress &&
                x.ConsumedUnits == session.ConsumedUnits &&
                x.UnpaidUnits == session.UnpaidUnits &&
                x.Signature.Equals(new Signature(1, 1, 27))));
        }

        [Test]
        public async Task send_should_succeed_when_receipt_is_valid()
        {
            var timestamp = _timestamper.UnixTime.Seconds;
            var receipt = GetDataDeliveryReceiptRequest();
            var deposit = GetDepositDetails();
            var session = GetConsumerSession();
            var provider = Substitute.For<INdmPeer>();
            var receiptId = Keccak.Compute(Rlp.Encode(Rlp.Encode(receipt.DepositId), Rlp.Encode(receipt.Number),
                Rlp.Encode(timestamp)).Bytes);
            _depositProvider.GetAsync(receipt.DepositId).Returns(deposit);
            _sessionService.GetActive(receipt.DepositId).Returns(session);
            _providerService.GetPeer(deposit.DataAsset.Provider.Address).Returns(provider);
            _receiptRequestValidator.IsValid(receipt, session.UnpaidUnits, session.ConsumedUnits,
                deposit.Deposit.Units).Returns(true);
            _ecdsa.RecoverPublicKey(Arg.Any<Signature>(), Arg.Any<Keccak>())
                .Returns(session.ConsumerNodeId);
            await _receiptService.SendAsync(receipt, 0, 0);
            await _sessionRepository.Received().UpdateAsync(session);
            await _receiptRepository.Received().AddAsync(Arg.Is<DataDeliveryReceiptDetails>(x =>
                x.Id == receiptId &&
                x.SessionId == session.Id &&
                x.DataAssetId == session.DataAssetId &&
                x.ConsumerNodeId == _nodePublicKey &&
                x.Request.Equals(receipt) &&
                x.Timestamp == timestamp &&
                !x.IsClaimed));
            provider.Received().SendDataDeliveryReceipt(receipt.DepositId, Arg.Is<DataDeliveryReceipt>(x =>
                x.StatusCode == StatusCodes.Ok));
        }

        private static DataDeliveryReceiptRequest GetDataDeliveryReceiptRequest()
        {
            var receipt = new DataDeliveryReceiptRequest(1, TestItem.KeccakA, new UnitsRange(0, 5));

            return receipt;
        }

        private static DepositDetails GetDepositDetails(uint timestamp = 0)
            => new DepositDetails(new Deposit(TestItem.KeccakA, 1, 1, 1),
                GetDataAsset(DataAssetUnitType.Unit), TestItem.AddressA, Array.Empty<byte>(), 1,
                new []{TransactionInfo.Default(TestItem.KeccakA, 1, 1, 1, 1)}, timestamp);

        private static DataAsset GetDataAsset(DataAssetUnitType unitType)
            => new DataAsset(Keccak.OfAnEmptyString, "test", "test", 1,
                unitType, 0, 10, new DataAssetRules(new DataAssetRule(1)),
                new DataAssetProvider(Address.Zero, "test"));
        
        private static ConsumerSession GetConsumerSession()
            => new ConsumerSession(Keccak.Zero, TestItem.KeccakA, TestItem.KeccakB, TestItem.AddressA,
                TestItem.PublicKeyA, TestItem.AddressB, TestItem.PublicKeyB, SessionState.Started,
                0, 0, dataAvailability: DataAvailability.Available);
    }
}
