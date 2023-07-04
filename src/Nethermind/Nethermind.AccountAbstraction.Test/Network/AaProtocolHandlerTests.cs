// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Network;
using Nethermind.AccountAbstraction.Source;
using Nethermind.AccountAbstraction.Broadcaster;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AccountAbstraction.Test.Network
{
    [TestFixture, Parallelizable(ParallelScope.Self)]
    public class AaProtocolHandlerTests
    {
        private ISession? _session;
        private IMessageSerializationService? _svc;
        private IDictionary<Address, IUserOperationPool>? _userOperationPools;
        private AaProtocolHandler? _handler;

        [SetUp]
        public void Setup()
        {
            _svc = new Nethermind.Network.Test.Builders.SerializationBuilder()
                .With(new UserOperationsMessageSerializer()).TestObject;

            NetworkDiagTracer.IsEnabled = true;

            _session = Substitute.For<ISession>();
            Node node = new(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
            _session.Node.Returns(node);
            _userOperationPools = new Dictionary<Address, IUserOperationPool>();
            UserOperationBroadcaster _broadcaster = new UserOperationBroadcaster(NullLogger.Instance);
            AccountAbstractionPeerManager _peerManager = new AccountAbstractionPeerManager(_userOperationPools, _broadcaster, NullLogger.Instance);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();

            _handler = new AaProtocolHandler(
                _session,
                _svc,
                new NodeStatsManager(timerFactory, LimboLogs.Instance),
                _userOperationPools,
                _peerManager,
                LimboLogs.Instance);
            _handler.Init();

            AccountAbstractionRpcModule aaRpcModule = new(_userOperationPools, new Address[] { });


        }

        [TearDown]
        public void TearDown()
        {
            _handler!.Dispose();
        }

        [Test]
        public void Metadata_correct()
        {
            _handler!.ProtocolCode.Should().Be("aa");
            _handler.Name.Should().Be("aa");
            _handler.ProtocolVersion.Should().Be(0);
            _handler.MessageIdSpaceSize.Should().Be(4);
        }

        [Test]
        public void Can_handle_user_operations_message()
        {
            bool parsed = Address.TryParse("0xdb8b5f6080a8e466b64a8d7458326cb650b3353f", out Address? _ep1);
            parsed = Address.TryParse("0x90f3e1105e63c877bf9587de5388c23cdb702c6b", out Address? _ep2);
            Address ep1 = _ep1 ?? throw new ArgumentNullException(nameof(_ep1));
            Address ep2 = _ep2 ?? throw new ArgumentNullException(nameof(_ep2));
            IList<UserOperationWithEntryPoint> uOps = new List<UserOperationWithEntryPoint>();
            uOps.Add(new UserOperationWithEntryPoint(Build.A.UserOperation.SignedAndResolved().TestObject, ep1));
            uOps.Add(new UserOperationWithEntryPoint(Build.A.UserOperation.SignedAndResolved().TestObject, ep2));
            UserOperationsMessage msg = new UserOperationsMessage(uOps);
            HandleZeroMessage(msg, AaMessageCode.UserOperations);
        }

        private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
        {
            IByteBuffer uOpsPacket = _svc!.ZeroSerialize(msg);
            uOpsPacket.ReadByte();
            _handler!.HandleMessage(new ZeroPacket(uOpsPacket) { PacketType = (byte)messageCode });
        }
    }
}
