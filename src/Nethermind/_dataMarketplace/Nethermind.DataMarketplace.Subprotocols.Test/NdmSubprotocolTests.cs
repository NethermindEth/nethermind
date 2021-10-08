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

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.Rlpx;
using Nethermind.Specs;
using Nethermind.Stats;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;
using Session = Nethermind.DataMarketplace.Core.Domain.Session;
using SessionState = Nethermind.DataMarketplace.Core.Domain.SessionState;

namespace Nethermind.DataMarketplace.Subprotocols.Test
{
    [TestFixture]
    public class NdmSubprotocolTests
    {
        private IMessageSerializationService _service = SerializationService.WithAllSerializers;
        private NdmSubprotocol _subprotocol;

        private void BuildSubprotocol()
        {
            ISession session = Substitute.For<ISession>();
            INodeStatsManager nodeStatsManager = new NodeStatsManager(Substitute.For<ITimerFactory>(), LimboLogs.Instance);
            MessageSerializationService serializationService = new MessageSerializationService();
            serializationService.Register(typeof(HiMessage).Assembly);
            IConsumerService consumerService = Substitute.For<IConsumerService>();
            INdmConsumerChannelManager consumerChannelManager = Substitute.For<INdmConsumerChannelManager>();
            EthereumEcdsa ecdsa = new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance);
            _subprotocol = new NdmSubprotocol(session, nodeStatsManager, serializationService, LimboLogs.Instance, consumerService, consumerChannelManager, ecdsa, new DevWallet(new WalletConfig(), LimboLogs.Instance), Substitute.For<INdmFaucet>(), TestItem.PublicKeyB, TestItem.AddressB, TestItem.AddressA, false);
        }

        [TearDown]
        public void TearDownAttribute()
        {
            _subprotocol?.Dispose();
        }

        [Test]
        public void Can_change_address()
        {
            BuildSubprotocol();
            _subprotocol.ChangeConsumerAddress(TestItem.AddressA);
            _subprotocol.ChangeProviderAddress(TestItem.AddressB);
            _subprotocol.ChangeHostConsumerAddress(TestItem.AddressC);
            _subprotocol.ChangeHostProviderAddress(TestItem.AddressD);
        }
        
        [Test]
        public void Can_init()
        {
            BuildSubprotocol();
            _subprotocol.Init();
        }

        [Test]
        public void Smoke_for_send_finish_session()
        {
            BuildSubprotocol();
            _subprotocol.SendFinishSession(TestItem.KeccakA);
        }

        [Test]
        public void Smoke_for_send_consumer_address_changed()
        {
            BuildSubprotocol();
            _subprotocol.SendConsumerAddressChanged(TestItem.AddressA);
        }

        [Test]
        public void Smoke_for_send_data_delivery_receipt()
        {
            BuildSubprotocol();
            _subprotocol.SendDataDeliveryReceipt(TestItem.KeccakA, new DataDeliveryReceipt(StatusCodes.Ok, 1, 1, new Signature(new byte[65])));
        }

        [Test]
        public void Smoke_for_send_data_request()
        {
            BuildSubprotocol();
            _subprotocol.SendDataRequestAsync(new DataRequest(TestItem.KeccakA, 1, 1, 1, new byte[0], TestItem.AddressA, TestItem.AddressB, new Signature(new byte[65])), 1, CancellationToken.None);
        }

        [Test]
        public void Smoke_for_send_disable_data_stream()
        {
            BuildSubprotocol();
            _subprotocol.SendDisableDataStream(Keccak.Zero, "client");
        }

        [Test]
        public void Smoke_for_send_enable_data_stream()
        {
            BuildSubprotocol();
            _subprotocol.SendEnableDataStream(Keccak.Zero, "client", new string[] {"a"});
        }

        [Test]
        public void Smoke_for_send_get_deposit_approvals()
        {
            BuildSubprotocol();
            _subprotocol.SendGetDepositApprovals(TestItem.KeccakA);
        }

        [Test]
        public void Smoke_for_send_request_deposit_approval()
        {
            BuildSubprotocol();
            _subprotocol.SendRequestDepositApproval(TestItem.KeccakA, TestItem.AddressA, "kyc");
        }

        [Test]
        public void Smoke_for_send_request_eth()
        {
            BuildSubprotocol();
            _subprotocol.SendRequestEthAsync(TestItem.AddressA, 1, CancellationToken.None);
        }

        [Test]
        public void Fails_if_receives_any_message_before_hi()
        {
            DataAsset dataAsset = new DataAsset(Keccak.OfAnEmptyString, "name", "description", 1, DataAssetUnitType.Time, 1000, 10000, new DataAssetRules(new DataAssetRule(1), new DataAssetRule(2)), new DataAssetProvider(Address.SystemUser, "provider"));
            Signature signature = new Signature(UInt256.One, UInt256.One, 27);
            Session session = new Session(Keccak.EmptyTreeHash, Keccak.OfAnEmptyString, Keccak.OfAnEmptySequenceRlp, Address.SystemUser, TestItem.PublicKeyA, TestItem.AddressB, TestItem.PublicKeyB, SessionState.ProviderDisconnected, 1, 2, 3, 4, 5, 6, 7, 8);

            TestBeforeHi(new ConsumerAddressChangedMessage(Address.SystemUser));
            TestBeforeHi(new DataAssetDataMessage(Keccak.OfAnEmptySequenceRlp, "client", "data", 1));
            TestBeforeHi(new DataAssetMessage(dataAsset));
            TestBeforeHi(new DataAssetRemovedMessage(Keccak.OfAnEmptyString));
            TestBeforeHi(new DataAssetsMessage(new[] {dataAsset, dataAsset}));

            TestBeforeHi(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.Archived));
            TestBeforeHi(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.UnderMaintenance));
            TestBeforeHi(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.Closed));
            TestBeforeHi(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.Published));
            TestBeforeHi(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.Unpublished));

            TestBeforeHi(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.Available));
            TestBeforeHi(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.Unknown));
            TestBeforeHi(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.SubscriptionEnded));
            TestBeforeHi(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.UnitsExceeded));
            TestBeforeHi(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.ExpiryRuleExceeded));
            TestBeforeHi(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.DataDeliveryReceiptInvalid));
            TestBeforeHi(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.DataDeliveryReceiptNotProvided));

            TestBeforeHi(new DataDeliveryReceiptMessage(Keccak.OfAnEmptyString, new DataDeliveryReceipt(StatusCodes.Error, 1, 2, signature)));
            TestBeforeHi(new DataRequestMessage(new DataRequest(Keccak.OfAnEmptyString, 1, 2, 3, new byte[] {4}, Address.SystemUser, TestItem.AddressA, signature), 5));
            TestBeforeHi(new DataRequestResultMessage(Keccak.OfAnEmptyString, DataRequestResult.DepositUnverified));
            TestBeforeHi(new DataStreamDisabledMessage(Keccak.OfAnEmptyString, "client"));
            TestBeforeHi(new DataStreamEnabledMessage(Keccak.OfAnEmptyString, "client", new string[] {"a", "b", "c"}));
            TestBeforeHi(new DepositApprovalConfirmedMessage(Keccak.OfAnEmptyString, Address.SystemUser));
            TestBeforeHi(new DepositApprovalRejectedMessage(Keccak.OfAnEmptyString, Address.SystemUser));
            TestBeforeHi(new DepositApprovalsMessage(new DepositApproval[] {new DepositApproval(Keccak.OfAnEmptyString, "asset", "kyc", Address.SystemUser, TestItem.AddressA, 1, DepositApprovalState.Confirmed)}));
            TestBeforeHi(new DisableDataStreamMessage(Keccak.OfAnEmptyString, "client"));
            TestBeforeHi(new EarlyRefundTicketMessage(new EarlyRefundTicket(Keccak.OfAnEmptyString, 1, signature), RefundReason.InvalidDataAsset));
            TestBeforeHi(new EnableDataStreamMessage(Keccak.OfAnEmptyString, "client", new string[] {"a", "b", "c"}));
            TestBeforeHi(new EthRequestedMessage(new FaucetResponse(FaucetRequestStatus.FaucetDisabled, FaucetRequestDetails.Empty)));
            TestBeforeHi(new FinishSessionMessage(Keccak.OfAnEmptyString));
            TestBeforeHi(new GetDataAssetsMessage());
            TestBeforeHi(new GetDepositApprovalsMessage());
            TestBeforeHi(new GraceUnitsExceededMessage(Keccak.EmptyTreeHash, 1, 2));
            TestBeforeHi(new InvalidDataMessage(Keccak.OfAnEmptyString, InvalidDataReason.InvalidResult));
            TestBeforeHi(new ProviderAddressChangedMessage(Address.SystemUser));
            TestBeforeHi(new RequestDataDeliveryReceiptMessage(new DataDeliveryReceiptRequest(1, Keccak.OfAnEmptyString, new UnitsRange(2, 3), true, new[] {new DataDeliveryReceiptToMerge(new UnitsRange(7, 8), signature)})));
            TestBeforeHi(new RequestDepositApprovalMessage(Keccak.OfAnEmptyString, Address.SystemUser, "kyc"));
            TestBeforeHi(new RequestEthMessage(Address.SystemUser, UInt256.One));
            TestBeforeHi(new SessionFinishedMessage(session));
            TestBeforeHi(new SessionStartedMessage(session));
        }

        [Test]
        public void Can_handle_any_message()
        {
            DataAsset dataAsset = new DataAsset(Keccak.OfAnEmptyString, "name", "description", 1, DataAssetUnitType.Time, 1000, 10000, new DataAssetRules(new DataAssetRule(1), new DataAssetRule(2)), new DataAssetProvider(Address.SystemUser, "provider"));
            Signature signature = new Signature(UInt256.One, UInt256.One, 27);
            Session session = new Session(Keccak.EmptyTreeHash, Keccak.OfAnEmptyString, Keccak.OfAnEmptySequenceRlp, Address.SystemUser, TestItem.PublicKeyA, TestItem.AddressB, TestItem.PublicKeyB, SessionState.ProviderDisconnected, 1, 2, 3, 4, 5, 6, 7, 8);

            SmokeTestAfterHi(new ConsumerAddressChangedMessage(Address.SystemUser));
            SmokeTestAfterHi(new DataAssetDataMessage(Keccak.OfAnEmptySequenceRlp, "client", "data", 1));
            SmokeTestAfterHi(new DataAssetMessage(dataAsset));
            SmokeTestAfterHi(new DataAssetRemovedMessage(Keccak.OfAnEmptyString));
            SmokeTestAfterHi(new DataAssetsMessage(new[] {dataAsset, dataAsset}));

            SmokeTestAfterHi(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.Archived));
            SmokeTestAfterHi(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.UnderMaintenance));
            SmokeTestAfterHi(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.Closed));
            SmokeTestAfterHi(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.Published));
            SmokeTestAfterHi(new DataAssetStateChangedMessage(Keccak.OfAnEmptyString, DataAssetState.Unpublished));

            SmokeTestAfterHi(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.Available));
            SmokeTestAfterHi(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.Unknown));
            SmokeTestAfterHi(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.SubscriptionEnded));
            SmokeTestAfterHi(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.UnitsExceeded));
            SmokeTestAfterHi(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.ExpiryRuleExceeded));
            SmokeTestAfterHi(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.DataDeliveryReceiptInvalid));
            SmokeTestAfterHi(new DataAvailabilityMessage(Keccak.OfAnEmptyString, DataAvailability.DataDeliveryReceiptNotProvided));

            SmokeTestAfterHi(new DataDeliveryReceiptMessage(Keccak.OfAnEmptyString, new DataDeliveryReceipt(StatusCodes.Error, 1, 2, signature)));
            SmokeTestAfterHi(new DataRequestMessage(new DataRequest(Keccak.OfAnEmptyString, 1, 2, 3, new byte[] {4}, Address.SystemUser, TestItem.AddressA, signature), 5));
            SmokeTestAfterHi(new DataRequestResultMessage(Keccak.OfAnEmptyString, DataRequestResult.DepositUnverified));
            SmokeTestAfterHi(new DataStreamDisabledMessage(Keccak.OfAnEmptyString, "client"));
            SmokeTestAfterHi(new DataStreamEnabledMessage(Keccak.OfAnEmptyString, "client", new string[] {"a", "b", "c"}));
            SmokeTestAfterHi(new DepositApprovalConfirmedMessage(Keccak.OfAnEmptyString, Address.SystemUser));
            SmokeTestAfterHi(new DepositApprovalRejectedMessage(Keccak.OfAnEmptyString, Address.SystemUser));
            SmokeTestAfterHi(new DepositApprovalsMessage(new DepositApproval[] {new DepositApproval(Keccak.OfAnEmptyString, "asset", "kyc", Address.SystemUser, TestItem.AddressA, 1, DepositApprovalState.Confirmed)}));
            SmokeTestAfterHi(new DisableDataStreamMessage(Keccak.OfAnEmptyString, "client"));
            SmokeTestAfterHi(new EarlyRefundTicketMessage(new EarlyRefundTicket(Keccak.OfAnEmptyString, 1, signature), RefundReason.InvalidDataAsset));
            SmokeTestAfterHi(new EnableDataStreamMessage(Keccak.OfAnEmptyString, "client", new string[] {"a", "b", "c"}));
            SmokeTestAfterHi(new EthRequestedMessage(new FaucetResponse(FaucetRequestStatus.FaucetDisabled, FaucetRequestDetails.Empty)));
            SmokeTestAfterHi(new FinishSessionMessage(Keccak.OfAnEmptyString));
            SmokeTestAfterHi(new GetDataAssetsMessage());
            SmokeTestAfterHi(new GetDepositApprovalsMessage());
            SmokeTestAfterHi(new GraceUnitsExceededMessage(Keccak.EmptyTreeHash, 1, 2));
            SmokeTestAfterHi(new InvalidDataMessage(Keccak.OfAnEmptyString, InvalidDataReason.InvalidResult));
            SmokeTestAfterHi(new ProviderAddressChangedMessage(Address.SystemUser));
            SmokeTestAfterHi(new RequestDataDeliveryReceiptMessage(new DataDeliveryReceiptRequest(1, Keccak.OfAnEmptyString, new UnitsRange(2, 3), true, new[] {new DataDeliveryReceiptToMerge(new UnitsRange(7, 8), signature)})));
            SmokeTestAfterHi(new RequestDepositApprovalMessage(Keccak.OfAnEmptyString, Address.SystemUser, "kyc"));
            SmokeTestAfterHi(new RequestEthMessage(Address.SystemUser, UInt256.One));
            SmokeTestAfterHi(new SessionFinishedMessage(session));
            SmokeTestAfterHi(new SessionStartedMessage(session));
        }

        public void TestBeforeHi<T>(T message) where T : P2PMessage
        {
            BuildSubprotocol();
            byte[] bytes = _service.Serialize(message);
            Packet packet = new Packet(bytes);
            packet.PacketType = message.PacketType;
            Assert.Throws<SubprotocolException>(() => _subprotocol.HandleMessage(packet));
        }

        public void SmokeTestAfterHi<T>(T message) where T : P2PMessage
        {
            if (NdmMessageCode.IsProviderOnly(message.PacketType))
            {
                return;
            }

            BuildSubprotocol();
            EthereumEcdsa ecdsa = new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance);
            Signature signature = ecdsa.Sign(TestItem.PrivateKeyA, Keccak.Zero);
            HiMessage hiMessage = new HiMessage(1, TestItem.AddressA, TestItem.AddressB, TestItem.PublicKeyA, signature);
            byte[] hiBytes = _service.Serialize(hiMessage);
            _subprotocol.HandleMessage(new Packet(hiBytes));
            byte[] bytes = _service.Serialize(message);
            Packet packet = new Packet(bytes);
            packet.PacketType = message.PacketType;

            if (NdmMessageCode.IsRequestResponse(message.PacketType))
            {
                Assert.Throws<SubprotocolException>(() => _subprotocol.HandleMessage(packet));
            }
            else
            {
                _subprotocol.HandleMessage(packet);
            }
        }
    }
}
