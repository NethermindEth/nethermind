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
// 

using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
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
