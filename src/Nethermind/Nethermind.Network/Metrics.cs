/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

namespace Nethermind.Network
{
    public class Metrics
    {
        public static long IncomingConnections { get; set; }
        public static long OutgoingConnections { get; set; }
        public static long Handshakes { get; set; }
        public static long HandshakeTimeouts { get; set; }
        public static long HellosReceived { get; set; }
        public static long HellosSent { get; set; }
        public static long StatusesReceived { get; set; }
        public static long StatusesSent { get; set; }
        public static long BreachOfProtocolDisconnects { get; set; }
        public static long UselessPeerDisconnects { get; set; }
        public static long TooManyPeersDisconnects { get; set; }
        public static long AlreadyConnectedDisconnects { get; set; }
        public static long IncompatibleP2PDisconnects { get; set; }
        public static long ReceiveMessageTimeoutDisconnects { get; set; }
        public static long UnexpectedIdentityDisconnects { get; set; }
        public static long NullNodeIdentityDisconnects { get; set; }
        public static long ClientQuittingDisconnects { get; set; }
        public static long OtherDisconnects { get; set; }
        public static long DiconnectRequestedDisconnects { get; set; }
        public static long SameAsSelfDisconnects { get; set; }
        public static long TcpSubsystemErrorDisconnects { get; set; }
    }
}