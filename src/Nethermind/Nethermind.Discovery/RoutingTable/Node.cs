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

using System;
using System.Net;
using Nethermind.Core.Crypto;

namespace Nethermind.Discovery.RoutingTable
{
    public class Node
    {
        public Node(PublicKey id)
        {
            Id = id;
            //Bytes or PrefixBytes?

            IdHash = Keccak.Compute(id.PrefixedBytes);
            IdHashText = IdHash.ToString();
        }

        //id is bytes without prefix byte - 64 bytes
        public PublicKey Id { get; }
        public Keccak IdHash { get; }
        public string IdHashText { get; }
        public string Host { get; private set; }
        public int Port { get; private set; }
        public IPEndPoint Address { get; private set; }
        public bool IsDicoveryNode { get; set; }

        public void InitializeAddress(IPEndPoint address)
        {
            Host = address.Address.ToString();
            Port = address.Port;
            Address = address;
        }

        public void InitializeAddress(string host, int port)
        {
            Host = host;
            Port = port;
            Address = new IPEndPoint(IPAddress.Parse(host), port);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }
            
            if (obj is Node item)
            {
                return string.Compare(IdHashText, item.IdHashText, StringComparison.InvariantCultureIgnoreCase) == 0;
            }

            return false;
        }

        public override int GetHashCode()
        {
            return IdHashText.GetHashCode();
        }

        public override string ToString()
        {
            return $"Id: {Id}, Host: {Host}, RemotePort: {Port}, IsDiscovery: {IsDicoveryNode}";
        }
    }
}