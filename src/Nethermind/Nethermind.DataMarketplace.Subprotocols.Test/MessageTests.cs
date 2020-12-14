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

using System.IO;
using System.Reflection;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Int256;
using Nethermind.Network;
using Nethermind.Network.P2P;
using NUnit.Framework;
using Session = Nethermind.DataMarketplace.Core.Domain.Session;
using SessionState = Nethermind.DataMarketplace.Core.Domain.SessionState;

namespace Nethermind.DataMarketplace.Subprotocols.Test
{
    [TestFixture]
    public class MessageTests
    {
        private IMessageSerializationService _service = SerializationService.WithAllSerializers;

        [Test]
        public void Message_have_valid_protocol_and_can_serialize_and_deserialize()
        {
            DataAsset dataAsset = new DataAsset(Keccak.OfAnEmptyString, "name", "description", 1, DataAssetUnitType.Time, 1000, 10000, new DataAssetRules(new DataAssetRule(1), new DataAssetRule(2)), new DataAssetProvider(Address.SystemUser, "provider"));
            Signature signature = new Signature(UInt256.One, UInt256.One, 27);
            Session session = new Session(Keccak.EmptyTreeHash, Keccak.OfAnEmptyString, Keccak.OfAnEmptySequenceRlp, Address.SystemUser, TestItem.PublicKeyA, TestItem.AddressB, TestItem.PublicKeyB, SessionState.ProviderDisconnected, 1, 2, 3, 4, 5, 6, 7, 8);

            Test(new ConsumerAddressChangedMessage(Address.SystemUser));
            Test(new DataAssetDataMessage(Keccak.OfAnEmptySequenceRlp, "client", "data", 1));
            Test(new DataAssetMessage(dataAsset));
            Test(new DataAssetRemovedMessage(Keccak.OfAnEmptyString));
            Test(new DataAssetsMessage(new[] {dataAsset, dataAsset}));

            Test(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.Archived));
            Test(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.UnderMaintenance));
            Test(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.Closed));
            Test(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.Published));
            Test(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.Unpublished));

            Test(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.Available));
            Test(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.Unknown));
            Test(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.SubscriptionEnded));
            Test(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.UnitsExceeded));
            Test(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.ExpiryRuleExceeded));
            Test(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.DataDeliveryReceiptInvalid));
            Test(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.DataDeliveryReceiptNotProvided));

            Test(new DataDeliveryReceiptMessage(Keccak.OfAnEmptyString, new DataDeliveryReceipt(StatusCodes.Error, 1, 2, signature)));
            Test(new DataRequestMessage(new DataRequest(Keccak.OfAnEmptyString, 1, 2, 3, new byte[] {4}, Address.SystemUser, TestItem.AddressA, signature), 5));
            Test(new DataRequestResultMessage(Keccak.OfAnEmptyString, DataRequestResult.DepositUnverified));
            Test(new DataStreamDisabledMessage(Keccak.OfAnEmptyString, "client"));
            Test(new DataStreamEnabledMessage(Keccak.OfAnEmptyString, "client", new string[] {"a", "b", "c"}));
            Test(new DepositApprovalConfirmedMessage(Keccak.OfAnEmptyString, Address.SystemUser));
            Test(new DepositApprovalRejectedMessage(Keccak.OfAnEmptyString, Address.SystemUser));
            Test(new DepositApprovalsMessage(new DepositApproval[] {new DepositApproval(Keccak.OfAnEmptyString, "asset", "kyc", Address.SystemUser, TestItem.AddressA, 1, DepositApprovalState.Confirmed)}));
            Test(new DisableDataStreamMessage(Keccak.OfAnEmptyString, "client"));
            Test(new EarlyRefundTicketMessage(new EarlyRefundTicket(Keccak.OfAnEmptyString, 1, signature), RefundReason.InvalidDataAsset));
            Test(new EnableDataStreamMessage(Keccak.OfAnEmptyString, "client", new string[] {"a", "b", "c"}));
            Test(new EthRequestedMessage(new FaucetResponse(FaucetRequestStatus.FaucetDisabled, FaucetRequestDetails.Empty)));
            Test(new FinishSessionMessage(Keccak.OfAnEmptyString));
            Test(new GetDataAssetsMessage());
            Test(new GetDepositApprovalsMessage());
            Test(new GraceUnitsExceededMessage(Keccak.EmptyTreeHash, 1, 2));
            Test(new HiMessage(1, TestItem.AddressA, TestItem.AddressB, TestItem.PublicKeyA, signature));
            Test(new InvalidDataMessage(Keccak.OfAnEmptyString, InvalidDataReason.InvalidResult));
            Test(new ProviderAddressChangedMessage(Address.SystemUser));
            Test(new RequestDataDeliveryReceiptMessage(new DataDeliveryReceiptRequest(1, Keccak.OfAnEmptyString, new UnitsRange(2, 3), true, new[] {new DataDeliveryReceiptToMerge(new UnitsRange(7, 8), signature)})));
            Test(new RequestDepositApprovalMessage(Keccak.OfAnEmptyString, Address.SystemUser, "kyc"));
            Test(new RequestEthMessage(Address.SystemUser, UInt256.One));
            Test(new SessionFinishedMessage(session));
            Test(new SessionStartedMessage(session));
        }

        [Test]
        public void Invalid_messages_should_fail()
        {
            Assert.Throws<InvalidDataException>(() => Test(new ConsumerAddressChangedMessage(null)));
            Assert.Throws<InvalidDataException>(() => Test(new DataAssetDataMessage(null, "client", "data", 1)));
            Assert.Throws<InvalidDataException>(() => Test(new DataAssetMessage(null)));
            Assert.Throws<InvalidDataException>(() => Test(new DataAssetsMessage(new DataAsset[] {null})));
        }

        private void Test<T>(T message) where T : P2PMessage
        {
            message.Protocol.Should().Be("ndm");

            FieldInfo fieldInfo = typeof(NdmMessageCode).GetField(message.GetType().Name.Replace("Message", string.Empty), BindingFlags.Static | BindingFlags.Public);
            message.PacketType.Should().Be((int) fieldInfo.GetValue(null));

            byte[] firstSer = _service.Serialize(message);
            byte[] secondSer = _service.Serialize(_service.Deserialize<T>(firstSer));

            firstSer.Should().BeEquivalentTo(secondSer, typeof(T).Name + " -> " + firstSer.ToHexString());
        }
    }
}
