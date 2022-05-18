//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Net;
using Nethermind.Config;
using Nethermind.Core.Crypto;

namespace Nethermind.Stats.Model
{
    /// <summary>
    /// Represents a physical network node address and attributes that we assign to it (static, bootnode, trusted, etc.)
    /// </summary>
    public class Node : IFormattable
    {
        private string _clientId;
        private string _paddedHost;
        private string _paddedPort;

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
        /// We use bootnodes to bootstrap the discovery process.
        /// </summary>
        public bool IsBootnode { get; set; }

        /// <summary>
        /// We try to maintain connection with static nodes at all time.
        /// </summary>
        public bool IsStatic { get; set; }

        public string ClientId
        {
            get => _clientId;
            set
            {
                if (_clientId is null)
                {
                    _clientId = value;
                    RecognizeClientType();
                }
            }
        }

        public NodeClientType ClientType { get; private set; } = NodeClientType.Unknown;

        public string EthDetails { get; set; }
        public long CurrentReputation { get; set; }

        public Node(PublicKey id, IPEndPoint address)
        {
            Id = id;
            IdHash = Keccak.Compute(Id.PrefixedBytes);
            SetIPEndPoint(address);
        }

        public Node(NetworkNode networkNode, bool isStatic = false)
            : this(networkNode.NodeId, networkNode.Host, networkNode.Port, isStatic)
        {
        }

        public Node(PublicKey id, string host, int port, bool isStatic = false)
        {
            Id = id;
            IdHash = Keccak.Compute(Id.PrefixedBytes);
            SetIPEndPoint(host, port);
            IsStatic = isStatic;
        }

        private void SetIPEndPoint(IPEndPoint address)
        {
            Host = address.Address.MapToIPv4().ToString();
            Port = address.Port;
            Address = address;
            // xxx.xxx.xxx.xxx = 15
            _paddedHost = Host.PadLeft(15, ' ');
            // Port are up to 65535 => 5 chars
            _paddedPort = Port.ToString().PadLeft(5, ' ');
        }

        private void SetIPEndPoint(string host, int port)
        {
            SetIPEndPoint(new IPEndPoint(IPAddress.Parse(host), port));
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

        public override int GetHashCode() => HashCode.Combine(Id);

        public override string ToString() => ToString("p");

        public string ToString(string format) => ToString(format, null);

        public string ToString(string format, IFormatProvider formatProvider)
        {
            return format switch
            {
                "s" => $"{_paddedHost}:{_paddedPort}",
                "c" => $"[Node|{_paddedHost}:{_paddedPort}|{EthDetails}|{ClientId}]",
                "f" => $"enode://{Id.ToString(false)}@{_paddedHost}:{_paddedPort}|{ClientId}",
                "e" => $"enode://{Id.ToString(false)}@{_paddedHost}:{_paddedPort}",
                "p" => $"enode://{Id.ToString(false)}@{_paddedHost}:{_paddedPort}|{Id.Address}",
                _ => $"enode://{Id.ToString(false)}@{_paddedHost}:{_paddedPort}"
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

        private void RecognizeClientType()
        {
            if (_clientId is null)
            {
                ClientType = NodeClientType.Unknown;
            }
            else if (_clientId.Contains("BeSu", StringComparison.InvariantCultureIgnoreCase))
            {
                ClientType = NodeClientType.BeSu;
            }
            else if (_clientId.Contains("Geth", StringComparison.InvariantCultureIgnoreCase))
            {
                ClientType = NodeClientType.Geth;
            }
            else if (_clientId.Contains("Nethermind", StringComparison.InvariantCultureIgnoreCase))
            {
                ClientType = NodeClientType.Nethermind;
            }
            else if (_clientId.Contains("Parity", StringComparison.InvariantCultureIgnoreCase))
            {
                ClientType = NodeClientType.Parity;
            }
            else if (_clientId.Contains("OpenEthereum", StringComparison.InvariantCultureIgnoreCase))
            {
                ClientType = NodeClientType.OpenEthereum;
            }
            else if (_clientId.Contains("Trinity", StringComparison.InvariantCultureIgnoreCase))
            {
                ClientType = NodeClientType.Trinity;
            }
            else
            {
                ClientType = NodeClientType.Unknown;
            }
        }
    }
}
