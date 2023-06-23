// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Analyzers
{
    public class MetricsDisconnectsAnalyzer : IDisconnectsAnalyzer
    {
        public void ReportDisconnect(EthDisconnectReason reason, DisconnectType type, string details)
        {
            if (type == DisconnectType.Remote)
            {
                switch (reason)
                {
                    case EthDisconnectReason.BreachOfProtocol:
                        Metrics.BreachOfProtocolDisconnects++;
                        break;
                    case EthDisconnectReason.UselessPeer:
                        Metrics.UselessPeerDisconnects++;
                        break;
                    case EthDisconnectReason.TooManyPeers:
                        Metrics.TooManyPeersDisconnects++;
                        break;
                    case EthDisconnectReason.AlreadyConnected:
                        Metrics.AlreadyConnectedDisconnects++;
                        break;
                    case EthDisconnectReason.IncompatibleP2PVersion:
                        Metrics.IncompatibleP2PDisconnects++;
                        break;
                    case EthDisconnectReason.NullNodeIdentityReceived:
                        Metrics.NullNodeIdentityDisconnects++;
                        break;
                    case EthDisconnectReason.ClientQuitting:
                        Metrics.ClientQuittingDisconnects++;
                        break;
                    case EthDisconnectReason.UnexpectedIdentity:
                        Metrics.UnexpectedIdentityDisconnects++;
                        break;
                    case EthDisconnectReason.ReceiveMessageTimeout:
                        Metrics.ReceiveMessageTimeoutDisconnects++;
                        break;
                    case EthDisconnectReason.DisconnectRequested:
                        Metrics.DisconnectRequestedDisconnects++;
                        break;
                    case EthDisconnectReason.IdentitySameAsSelf:
                        Metrics.SameAsSelfDisconnects++;
                        break;
                    case EthDisconnectReason.TcpSubSystemError:
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
                    case EthDisconnectReason.BreachOfProtocol:
                        Metrics.LocalBreachOfProtocolDisconnects++;
                        break;
                    case EthDisconnectReason.UselessPeer:
                        Metrics.LocalUselessPeerDisconnects++;
                        break;
                    case EthDisconnectReason.TooManyPeers:
                        Metrics.LocalTooManyPeersDisconnects++;
                        break;
                    case EthDisconnectReason.AlreadyConnected:
                        Metrics.LocalAlreadyConnectedDisconnects++;
                        break;
                    case EthDisconnectReason.IncompatibleP2PVersion:
                        Metrics.LocalIncompatibleP2PDisconnects++;
                        break;
                    case EthDisconnectReason.NullNodeIdentityReceived:
                        Metrics.LocalNullNodeIdentityDisconnects++;
                        break;
                    case EthDisconnectReason.ClientQuitting:
                        Metrics.LocalClientQuittingDisconnects++;
                        break;
                    case EthDisconnectReason.UnexpectedIdentity:
                        Metrics.LocalUnexpectedIdentityDisconnects++;
                        break;
                    case EthDisconnectReason.ReceiveMessageTimeout:
                        Metrics.LocalReceiveMessageTimeoutDisconnects++;
                        break;
                    case EthDisconnectReason.DisconnectRequested:
                        Metrics.LocalDisconnectRequestedDisconnects++;
                        break;
                    case EthDisconnectReason.IdentitySameAsSelf:
                        Metrics.LocalSameAsSelfDisconnects++;
                        break;
                    case EthDisconnectReason.TcpSubSystemError:
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
