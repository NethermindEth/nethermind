// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Analyzers
{
    public class MetricsDisconnectsAnalyzer : IDisconnectsAnalyzer
    {
        public void ReportDisconnect(DisconnectReason reason, DisconnectType type, string details)
        {
            if (type == DisconnectType.Remote)
            {
                switch (reason)
                {
                    case DisconnectReason.BreachOfProtocol:
                        Metrics.BreachOfProtocolDisconnects++;
                        break;
                    case DisconnectReason.UselessPeer:
                        Metrics.UselessPeerDisconnects++;
                        break;
                    case DisconnectReason.TooManyPeers:
                        Metrics.TooManyPeersDisconnects++;
                        break;
                    case DisconnectReason.AlreadyConnected:
                        Metrics.AlreadyConnectedDisconnects++;
                        break;
                    case DisconnectReason.IncompatibleP2PVersion:
                        Metrics.IncompatibleP2PDisconnects++;
                        break;
                    case DisconnectReason.NullNodeIdentityReceived:
                        Metrics.NullNodeIdentityDisconnects++;
                        break;
                    case DisconnectReason.ClientQuitting:
                        Metrics.ClientQuittingDisconnects++;
                        break;
                    case DisconnectReason.UnexpectedIdentity:
                        Metrics.UnexpectedIdentityDisconnects++;
                        break;
                    case DisconnectReason.ReceiveMessageTimeout:
                        Metrics.ReceiveMessageTimeoutDisconnects++;
                        break;
                    case DisconnectReason.DisconnectRequested:
                        Metrics.DisconnectRequestedDisconnects++;
                        break;
                    case DisconnectReason.IdentitySameAsSelf:
                        Metrics.SameAsSelfDisconnects++;
                        break;
                    case DisconnectReason.TcpSubSystemError:
                        Metrics.TcpSubsystemErrorDisconnects++;
                        break;
                    default:
                        Metrics.OtherDisconnects++;
                        break;
                }
            }

            if (type == DisconnectType.Local)
            {
                switch (reason)
                {
                    case DisconnectReason.BreachOfProtocol:
                        Metrics.LocalBreachOfProtocolDisconnects++;
                        break;
                    case DisconnectReason.UselessPeer:
                        Metrics.LocalUselessPeerDisconnects++;
                        break;
                    case DisconnectReason.TooManyPeers:
                        Metrics.LocalTooManyPeersDisconnects++;
                        break;
                    case DisconnectReason.AlreadyConnected:
                        Metrics.LocalAlreadyConnectedDisconnects++;
                        break;
                    case DisconnectReason.IncompatibleP2PVersion:
                        Metrics.LocalIncompatibleP2PDisconnects++;
                        break;
                    case DisconnectReason.NullNodeIdentityReceived:
                        Metrics.LocalNullNodeIdentityDisconnects++;
                        break;
                    case DisconnectReason.ClientQuitting:
                        Metrics.LocalClientQuittingDisconnects++;
                        break;
                    case DisconnectReason.UnexpectedIdentity:
                        Metrics.LocalUnexpectedIdentityDisconnects++;
                        break;
                    case DisconnectReason.ReceiveMessageTimeout:
                        Metrics.LocalReceiveMessageTimeoutDisconnects++;
                        break;
                    case DisconnectReason.DisconnectRequested:
                        Metrics.LocalDisconnectRequestedDisconnects++;
                        break;
                    case DisconnectReason.IdentitySameAsSelf:
                        Metrics.LocalSameAsSelfDisconnects++;
                        break;
                    case DisconnectReason.TcpSubSystemError:
                        Metrics.LocalTcpSubsystemErrorDisconnects++;
                        break;
                    default:
                        Metrics.LocalOtherDisconnects++;
                        break;
                }
            }
        }
    }
}
