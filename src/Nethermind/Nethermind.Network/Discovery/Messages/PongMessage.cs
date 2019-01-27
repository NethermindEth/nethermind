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

using Nethermind.Core.Extensions;

namespace Nethermind.Network.Discovery.Messages
{
    public class PongMessage : DiscoveryMessage
    {
        public byte[] PingMdc { get; set; } //TODO: Should be Keccak?

        //time in seconds x seconds from now
        public long ExpirationTime { get; set; }

        public byte[] TopicHash { get; set; } //TODO: Should be Keccak?

        public uint TicketSerial { get; set; }

        public uint[] WaitPeriods { get; set; }

        public override string ToString()
        {
            return base.ToString() + $", PingMdc: {PingMdc?.ToHexString() ?? "empty"}, ExpirationTime {ExpirationTime}, TopicHash {TopicHash?.ToHexString() ?? "empty"}, TicketSerial {TIcketSerial}, WaitPeriods {String.Join(", ", Array.ConvertAll(WaitPeriods, x => x.ToString()))}";
        }

        public override MessageType MessageType => MessageType.Pong;
    }
}