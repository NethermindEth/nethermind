//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System.Net;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.AccountAbstraction.Network;
using Nethermind.AccountAbstraction.Source;
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
        private IUserOperationPool? _userOperationPool;
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
            _userOperationPool = Substitute.For<IUserOperationPool>();
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            _handler = new AaProtocolHandler(
                _session,
                _svc,
                new NodeStatsManager(timerFactory, LimboLogs.Instance),
                _userOperationPool,
                LimboLogs.Instance);
            _handler.Init();
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
        
        // [Test]
        // public void Can_handle_user_operations_message()
        // {
        //     UserOperationsMessage msg = new(new List<UserOperation>(Build.A.UserOperation.SignedAndResolved().TestObjectNTimes(3)));
        //     HandleZeroMessage(msg, AaMessageCode.UserOperations);
        // }
        
        private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
        {
            IByteBuffer getBlockHeadersPacket = _svc!.ZeroSerialize(msg);
            getBlockHeadersPacket.ReadByte();
            _handler!.HandleMessage(new ZeroPacket(getBlockHeadersPacket) {PacketType = (byte) messageCode});
        }
    }
}
