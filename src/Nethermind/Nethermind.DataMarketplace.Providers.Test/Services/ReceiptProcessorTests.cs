/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Peers;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.DataMarketplace.Providers.Services;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Providers.Test.Services
{
    internal class ReceiptProcessorTests
    {
        private IReceiptProcessor _processor;
        private IProviderSessionRepository _sessionRepository;
        private IAbiEncoder _encoder;
        private IEcdsa _signer;
        private INdmProviderPeer _peer;
        private ProviderSession _session;
        private PublicKey _publicKey;

        [SetUp]
        public void SetUp()
        {
            _sessionRepository = Substitute.For<IProviderSessionRepository>();
            _encoder = Substitute.For<IAbiEncoder>();
            _signer = Substitute.For<IEcdsa>();
            _peer = Substitute.For<INdmProviderPeer>();
            _publicKey = new PublicKey(new byte[64]);
            _session = new ProviderSession(Keccak.Zero, Keccak.Zero, Keccak.Zero, _publicKey.Address,
                _publicKey, TestItem.AddressB, TestItem.PublicKeyB, 0, 0);
            _session.SetDataAvailability(DataAvailability.Available);
            _session.Start(0);
            _processor = new ReceiptProcessor(_sessionRepository, _encoder, _signer, LimboLogs.Instance);
        }

        [Test]
        public async Task given_valid_consumer_address_delivery_receipt_should_be_processed()
        {
            _signer.RecoverPublicKey(Arg.Any<Signature>(), Arg.Any<Keccak>()).ReturnsForAnyArgs(_publicKey);
            _peer.ConsumerAddress.Returns(_publicKey.Address);

            var currentReceiptRequestUnitsRange = new UnitsRange(0, 9);
            var receiptRequest = new DataDeliveryReceiptRequest(1, Keccak.Zero, currentReceiptRequestUnitsRange);
            var deliveryReceipt = new DataDeliveryReceipt(StatusCodes.Ok, 0, 0, new Signature(new byte[65]));

            var wasProcessed = await _processor.TryProcessAsync(_session, _publicKey.Address, _peer, receiptRequest,
                deliveryReceipt);

            wasProcessed.Should().BeTrue();
            _session.DataAvailability.Should().NotBe(DataAvailability.DataDeliveryReceiptInvalid);
        }

        [Test]
        public async Task given_valid_consumer_address_and_payment_to_be_claimed_delivery_receipt_should_be_processed()
        {
            _signer.RecoverPublicKey(Arg.Any<Signature>(), Arg.Any<Keccak>()).ReturnsForAnyArgs(_publicKey);
            _peer.ConsumerAddress.Returns(_publicKey.Address);

            var currentReceiptRequestUnitsRange = new UnitsRange(0, 9);
            var receiptRequest = new DataDeliveryReceiptRequest(1, Keccak.Zero, currentReceiptRequestUnitsRange);
            var deliveryReceipt = new DataDeliveryReceipt(StatusCodes.Ok, 0, 0, new Signature(new byte[65]));

            var wasProcessed = await _processor.TryProcessAsync(_session, _publicKey.Address, _peer, receiptRequest,
                deliveryReceipt);

            wasProcessed.Should().BeTrue();
            _session.DataAvailability.Should().NotBe(DataAvailability.DataDeliveryReceiptInvalid);
        }

        [Test]
        public async Task given_invalid_consumer_address_delivery_receipt_should_not_be_processed()
        {
            _signer.RecoverPublicKey(Arg.Any<Signature>(), Arg.Any<Keccak>()).ReturnsForAnyArgs(TestItem.PublicKeyB);
            _peer.ConsumerAddress.Returns(TestItem.AddressC);

            var currentReceiptRequestUnitsRange = new UnitsRange(0, 9);
            var receiptRequest = new DataDeliveryReceiptRequest(1, Keccak.Zero, currentReceiptRequestUnitsRange);
            var deliveryReceipt = new DataDeliveryReceipt(StatusCodes.Ok, 0, 0, new Signature(new byte[65]));

            var wasProcessed = await _processor.TryProcessAsync(_session, _publicKey.Address, _peer, receiptRequest,
                deliveryReceipt);

            wasProcessed.Should().BeFalse();
            _session.DataAvailability.Should().Be(DataAvailability.DataDeliveryReceiptInvalid);
        }
    }
}