﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Diagnostics;
using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Stats.Model
{   
    /// <summary>
    /// Represents a physical network node address and attributes that we assign to it (static, bootnode, trusted, etc.)
    /// </summary>
    public class Node : IFormattable
    {
        /// <summary>
        /// Node public key - same as in enode. 
        /// </summary>
        public PublicKey Id { get; }

        /// <summary>
        /// Hash of the node ID used extensively in discovery and kept here to avoid rehashing.
        /// </summary>
        public Keccak IdHash { get; }
        
        /// <summary>
        /// Host part of the network node.
        /// </summary>
        public string Host { get; private set; }
        
        /// <summary>
        /// Port part of the network node.
        /// </summary>
        public int Port { get; set; }
        
        /// <summary>
        /// Network address of the node.
        /// </summary>
        public IPEndPoint Address { get; private set; }
        
        /// <summary>
        /// Means that the discovery process is aware of this network node. 
        /// </summary>
        public bool AddedToDiscovery { get; set; }
        
        /// <summary>
        /// We use bootnodes to bootstrap the discovery process.
        /// </summary>
        public bool IsBootnode { get; set; }
        
        /// <summary>
        /// We give trusted peers much better reputation upfront.
        /// </summary>
        public bool IsTrusted { get; set; }
        
        /// <summary>
        /// We try to maintain connection with static nodes at all time.
        /// </summary>
        public bool IsStatic { get; set; }
        public string ClientId { get; set; }
        public string EthDetails { get; set; }

        public Node(PublicKey id, IPEndPoint address)
        {
            Id = id;
            IdHash = Keccak.Compute(Id.PrefixedBytes);
            AddedToDiscovery = false;
            SetIPEndPoint(address);
        }

        public Node(PublicKey id, string host, int port, bool addedToDiscovery = false)
        {
            Id = id;
            IdHash = Keccak.Compute(Id.PrefixedBytes);
            AddedToDiscovery = addedToDiscovery;
            SetIPEndPoint(host, port);
        }

        public Node(string host, int port, bool isStatic = false)
        {
            Keccak512 socketHash = Keccak512.Compute($"{host}:{port}");
            Id = new PublicKey(socketHash.Bytes);
            IdHash = Keccak.Compute(Id.PrefixedBytes);
            AddedToDiscovery = true;
            IsStatic = isStatic;
            SetIPEndPoint(host, port);
        }

        private void SetIPEndPoint(IPEndPoint address)
        {
            Host = address.Address.ToString();
            Port = address.Port;
            Address = address;
        }

        private void SetIPEndPoint(string host, int port)
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
                return IdHash.Equals(item.IdHash);
            }

            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Id);
        }

        public override string ToString()
        {
            return $"enode://{Id.ToString(false)}@{Host}:{Port}|{Id.Address}";
        }

        public string ToString(string format)
        {
            return ToString(format, null);
        }  
       
        public string ToString(string format, IFormatProvider formatProvider)
        {
            string formattedHost = Host.Replace("::ffff:", string.Empty);
            return format switch
            {
                "s" => $"{formattedHost}:{Port}",
                "c" => $"[Node|{formattedHost}:{Port}|{ClientId}|{EthDetails}]",
                "f" => $"enode://{Id.ToString(false)}@{formattedHost}:{Port}|{ClientId}",
                _ => $"enode://{Id.ToString(false)}@{formattedHost}:{Port}"
            };
        }
        
        public static bool operator ==(Node a, Node b)
        {
            if (ReferenceEquals(a, null))
            {
                return ReferenceEquals(b, null);
            }

            if (ReferenceEquals(b, null))
            {
                return false;
            }

            return a.Id.Equals(b.Id);
        }

        public static bool operator !=(Node a, Node b)
        {
            return !(a == b);
        }
    }
}