// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

public class ProtocolValidatorTests
{
    [TestCase("besu", "besu/v23.4.0/linux-x86_64/openjdk-java-17", false)]
    [TestCase("besu", "Geth/v1.12.1-unstable-b8d7da87-20230808/linux-amd64/go1.19.2", true)]
    [TestCase("^((?!besu).)*$", "Geth/v1.12.1-unstable-b8d7da87-20230808/linux-amd64/go1.19.2", false)]
    [TestCase("^((?!besu).)*$", "besu/v23.4.0/linux-x86_64/openjdk-java-17", true)]
    public void On_hello_with_not_matching_client_id(string pattern, string clientId, bool shouldDisconnect)
    {
        ProtocolValidator protocolValidator = new ProtocolValidator(
            Substitute.For<INodeStatsManager>(),
            Substitute.For<IBlockTree>(),
            Substitute.For<IForkInfo>(),
            Substitute.For<IPeerManager>(),
            new NetworkConfig()
            {
                ClientIdMatcher = pattern
            },
            LimboLogs.Instance
        );

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(new NetworkNode("enode://0d837e193233c08d6950913bf69105096457fbe204679d6c6c021c36bb5ad83d167350440670e7fec189d80abc18076f45f44bfe480c85b6c632735463d34e4b@89.197.135.74:30303")));
        IProtocolHandler protocolHandler = Substitute.For<IProtocolHandler>();
        protocolValidator.DisconnectOnInvalid(Protocol.P2P, session, new P2PProtocolInitializedEventArgs(protocolHandler)
        {
            ClientId = clientId,
            P2PVersion = 5
        });
        if (shouldDisconnect)
        {
            session.Received(1).InitiateDisconnect(DisconnectReason.ClientFiltered, Arg.Any<string>());
        }
        else
        {
            session.DidNotReceive().InitiateDisconnect(DisconnectReason.ClientFiltered, Arg.Any<string>());
        }
    }

    [TestCase(11, 10, true)]
    [TestCase(10, 10, false)]
    [TestCase(9, 10, false)]
    public void On_max_active_peer_limit(int activePeerCount, int maxActivePeer, bool shouldDisconnect)
    {
        IPeerManager peerManager = Substitute.For<IPeerManager>();
        peerManager.MaxActivePeers.Returns(maxActivePeer);
        peerManager.ActivePeersCount.Returns(activePeerCount);

        ProtocolValidator protocolValidator = new ProtocolValidator(
            Substitute.For<INodeStatsManager>(),
            Substitute.For<IBlockTree>(),
            Substitute.For<IForkInfo>(),
            peerManager,
            new NetworkConfig(),
            LimboLogs.Instance
        );

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(new NetworkNode("enode://0d837e193233c08d6950913bf69105096457fbe204679d6c6c021c36bb5ad83d167350440670e7fec189d80abc18076f45f44bfe480c85b6c632735463d34e4b@89.197.135.74:30303")));
        IProtocolHandler protocolHandler = Substitute.For<IProtocolHandler>();
        protocolValidator.DisconnectOnInvalid(Protocol.P2P, session, new P2PProtocolInitializedEventArgs(protocolHandler)
        {
            P2PVersion = 5
        });

        if (shouldDisconnect)
        {
            session.Received(1).InitiateDisconnect(DisconnectReason.TooManyPeers, Arg.Any<string>());
        }
        else
        {
            session.DidNotReceive().InitiateDisconnect(DisconnectReason.TooManyPeers, Arg.Any<string>());
        }
    }
}
