// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using Nethermind.Xdc.P2P;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcProtocolValidatorTests
{
    private const ulong NetworkId = 51;
    private static readonly Hash256 GenesisHash = TestItem.KeccakA;

    [Test]
    public void Fork_id_is_required_on_xdc165_even_when_the_peer_claims_to_be_legacy()
    {
        // Status.ProtocolVersion is whatever the peer put there; only the negotiated version can gate the check.
        ISession session = Validate(XdcProtocolVersions.Xdc165, statusVersion: XdcProtocolVersions.Legacy, out bool valid);

        Assert.That(valid, Is.False);
        session.Received(1).InitiateDisconnect(DisconnectReason.MissingForkId, Arg.Any<string>());
    }

    [Test]
    public void Fork_id_is_not_required_on_the_legacy_version()
    {
        ISession session = Validate(XdcProtocolVersions.Legacy, statusVersion: XdcProtocolVersions.Legacy, out bool valid);

        Assert.That(valid, Is.True);
        session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public void Every_xdc_version_serves_node_data([Values(XdcProtocolVersions.Legacy, XdcProtocolVersions.Xdc164, XdcProtocolVersions.Xdc165)] byte version)
    {
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        Assert.That(CreateHandler(version, session).CanGetNodeData, Is.True);
    }

    private static ISession Validate(byte negotiatedVersion, byte statusVersion, out bool valid)
    {
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.NetworkId.Returns(NetworkId);
        blockTree.Head.Returns(Build.A.Block.WithNumber(0).TestObject);
        BlockHeader genesis = Build.A.BlockHeader.WithNumber(0).TestObject;
        genesis.Hash = GenesisHash;
        blockTree.Genesis.Returns(genesis);

        XdcProtocolValidator validator = new(
            Substitute.For<INodeStatsManager>(),
            blockTree,
            Substitute.For<IForkInfo>(),
            Substitute.For<INetworkConfig>(),
            LimboLogs.Instance);

        SyncPeerProtocolInitializedEventArgs args = new(CreateHandler(negotiatedVersion, session))
        {
            Protocol = Protocol.Eth,
            ProtocolVersion = statusVersion,
            NetworkId = NetworkId,
            GenesisHash = GenesisHash,
            ForkId = null,
        };

        valid = validator.ValidateOrDisconnect(Protocol.Eth, session, args);
        return session;
    }

    private static SyncPeerProtocolHandlerBase CreateHandler(byte version, ISession session)
    {
        ISyncServer syncServer = Substitute.For<ISyncServer>();
        syncServer.Head.Returns(Build.A.BlockHeader.WithNumber(0).TestObject);

        XdcConsensusMessageHandler.Factory consensusMessages = new(
            Substitute.For<ITimeoutCertificateManager>(), Substitute.For<IVotesManager>(),
            Substitute.For<ISyncInfoManager>(), Substitute.For<IBlockTree>(),
            Substitute.For<ISyncPeerPool>(), LimboLogs.Instance);

        IMessageSerializationService serializer = Substitute.For<IMessageSerializationService>();
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        IBackgroundTaskScheduler scheduler = Substitute.For<IBackgroundTaskScheduler>();
        ITxPool pool = Substitute.For<ITxPool>();
        IGossipPolicy gossip = Substitute.For<IGossipPolicy>();
        return version switch
        {
            XdcProtocolVersions.Legacy => new XdcProtocolHandler(consensusMessages, session, serializer, stats, syncServer, scheduler, pool, gossip, LimboLogs.Instance),
            XdcProtocolVersions.Xdc164 => new Xdc164ProtocolHandler(consensusMessages, session, serializer, stats, syncServer, scheduler, pool, gossip, Substitute.For<IForkInfo>(), LimboLogs.Instance),
            XdcProtocolVersions.Xdc165 => new Xdc165ProtocolHandler(consensusMessages, session, serializer, stats, syncServer, scheduler, pool, gossip, Substitute.For<IForkInfo>(), LimboLogs.Instance),
            _ => throw new ArgumentOutOfRangeException(nameof(version))
        };
    }
}
