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

using Nethermind.Core;
using Nethermind.Network;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class PeerInfo
    {
        public string ClientId { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Address { get; set; }
        public bool IsBootnode { get; set; }
        public bool IsTrusted { get; set; }
        public bool IsStatic { get; set; }
        public string Enode { get; set; }

        public PeerInfo()
        {
        }

        public PeerInfo(Peer peer)
        {
            ClientId = peer.Node.ClientId;
            Host = peer.Node.Host;
            Port = peer.Node.Port;
            Address = peer.Node.Address.ToString();
            IsBootnode = peer.Node.IsBootnode;
            IsTrusted = peer.Node.IsTrusted;
            IsStatic = peer.Node.IsStatic;
            Enode = peer.Node.ToString("e");
        }
    }
}