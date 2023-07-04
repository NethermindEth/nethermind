// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Wit;
using Nethermind.Network.P2P.Subprotocols.Wit.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Wit
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class WitProtocolHandlerTests
    {
        [Test]
        public void Protocol_code_is_wit()
        {
            Context context = new();
            context.WitProtocolHandler.ProtocolCode.Should().Be("wit");
        }

        [Test]
        public void Protocol_version_is_zero()
        {
            Context context = new();
            context.WitProtocolHandler.ProtocolVersion.Should().Be(0);
        }

        [Test]
        public void Message_space_should_be_correct()
        {
            int maxCode = typeof(WitMessageCode)
                .GetFields(BindingFlags.Static | BindingFlags.Public)
                .Where(f => f.FieldType == typeof(int))
                .Select(f => (int)f.GetValue(null)!).Max();

            Context context = new();
            context.WitProtocolHandler.MessageIdSpaceSize.Should().Be(maxCode + 1);
        }

        [Test]
        public void Name_should_be_wit0()
        {
            Context context = new();
            context.WitProtocolHandler.Name.Should().Be("wit0");
        }

        [Test]
        public void Init_does_nothing()
        {
            Context context = new();
            context.WitProtocolHandler.Init();
        }

        [Test]
        public void Can_disconnect()
        {
            Context context = new();
            context.WitProtocolHandler.DisconnectProtocol(DisconnectReason.Other, "just because");
            context.WitProtocolHandler.Dispose();
        }

        [Test]
        public void Init_timeout_should_timeout_eth()
        {
            Context context = new();
            bool initialized = false;
            context.WitProtocolHandler.ProtocolInitialized +=
                (s, e) => initialized = true;
            context.WitProtocolHandler.Init();
            initialized.Should().BeTrue();
        }

        [Test]
        public void Can_handle_request_for_an_empty_witness()
        {
            Context context = new();
            context.WitProtocolHandler.Init();

            GetBlockWitnessHashesMessage msg = new(5, Keccak.Zero);
            GetBlockWitnessHashesMessageSerializer serializer = new();
            var serialized = serializer.Serialize(msg);

            context.WitProtocolHandler.HandleMessage(new Packet("wit", WitMessageCode.GetBlockWitnessHashes, serialized));
            context.SyncServer.Received().GetBlockWitnessHashes(Keccak.Zero);
        }

        [Test]
        public void Can_handle_request_for_a_non_empty_witness()
        {
            Context context = new();
            context.SyncServer.GetBlockWitnessHashes(Keccak.Zero)
                .Returns(new[] { TestItem.KeccakA, TestItem.KeccakB });

            context.WitProtocolHandler.Init();

            GetBlockWitnessHashesMessage msg = new(5, Keccak.Zero);
            GetBlockWitnessHashesMessageSerializer serializer = new();
            var serialized = serializer.Serialize(msg);

            context.WitProtocolHandler.HandleMessage(new Packet("wit", WitMessageCode.GetBlockWitnessHashes, serialized));
            context.Session.Received().DeliverMessage(
                Arg.Is<BlockWitnessHashesMessage>(msg => msg.Hashes.Length == 2));
        }

        [Test]
        public async Task Can_request_non_empty_witness()
        {
            Context context = new();
            BlockWitnessHashesMessage msg = new(5, new[] { TestItem.KeccakA, TestItem.KeccakB });
            BlockWitnessHashesMessageSerializer serializer = new();
            var serialized = serializer.Serialize(msg);

            context.WitProtocolHandler.Init();


            context.Session
                // check if you did not enable the Init -> send message diag
                .When(s => s.DeliverMessage(
                    Arg.Is<GetBlockWitnessHashesMessage>(msg => msg.BlockHash == TestItem.KeccakA)))
                .Do(_ => context.WitProtocolHandler.HandleMessage(new Packet("wit", WitMessageCode.BlockWitnessHashes, serialized)));
            Task result = context.WitProtocolHandler.GetBlockWitnessHashes(TestItem.KeccakA, CancellationToken.None);
            await result;
            result.Status.Should().Be(TaskStatus.RanToCompletion);
        }

        private class Context
        {
            public ISession Session { get; }
            public ISyncServer SyncServer { get; }
            public WitProtocolHandler WitProtocolHandler { get; }

            public Context()
            {
                MessageSerializationService serializationService = new();
                serializationService.Register(typeof(GetBlockWitnessHashesMessage).Assembly);
                Session = Substitute.For<ISession>();
                Session.Node.Returns(new Node(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Loopback, 30303)));
                INodeStatsManager nodeStats = Substitute.For<INodeStatsManager>();
                SyncServer = Substitute.For<ISyncServer>();
                WitProtocolHandler = new WitProtocolHandler(
                    Session,
                    serializationService,
                    nodeStats,
                    SyncServer,
                    LimboLogs.Instance);
            }
        }
    }
}
