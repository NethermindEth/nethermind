// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using Nethermind.Config;
using Nethermind.Core.Crypto;

namespace Nethermind.Stats.Model
{
    /// <summary>
    /// Represents a physical network node address and attributes that we assign to it (static, bootnode, trusted, etc.)
    /// </summary>
    public sealed class Node : IFormattable, IEquatable<Node>
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
                    ClientType = RecognizeClientType(_clientId);
                }
            }
        }

        public NodeClientType ClientType { get; private set; } = NodeClientType.Unknown;

        public string EthDetails { get; set; }
        public long CurrentReputation { get; set; }

        public Node(NetworkNode networkNode, bool isStatic = false)
            : this(networkNode.NodeId, networkNode.Host, networkNode.Port, isStatic)
        {
        }

        public Node(PublicKey id, string host, int port, bool isStatic = false)
            : this(id, GetIPEndPoint(host, port), isStatic)
        {
        }

        public Node(PublicKey id, IPEndPoint address, bool isStatic = false)
        {
            Id = id;
            IdHash = Keccak.Compute(Id.PrefixedBytes);
            IsStatic = isStatic;
            SetIPEndPoint(address);
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

        private static IPEndPoint GetIPEndPoint(string host, int port)
        {
            return new IPEndPoint(IPAddress.Parse(host), port);
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

        public override int GetHashCode() => Id.GetHashCode();

        public override string ToString() => ToString(Format.WithPublicKey);

        public string ToString(string format) => ToString(format, null);

        public string ToString(string format, IFormatProvider formatProvider)
        {
            return format switch
            {
                Format.Short => $"{Host}:{Port}",
                Format.AlignedShort => $"{_paddedHost}:{_paddedPort}",
                Format.Console => $"[Node|{Host}:{Port}|{EthDetails}|{ClientId}]",
                Format.WithId => $"enode://{Id.ToString(false)}@{Host}:{Port}|{ClientId}",
                Format.ENode => $"enode://{Id.ToString(false)}@{Host}:{Port}",
                Format.WithPublicKey => $"enode://{Id.ToString(false)}@{Host}:{Port}|{Id.Address}",
                _ => $"enode://{Id.ToString(false)}@{Host}:{Port}"
            };
        }

        public bool Equals(Node other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;

            return Id.Equals(other.Id);
        }

        public static bool operator ==(Node a, Node b)
        {
            if (ReferenceEquals(a, b)) return true;

            if (ReferenceEquals(a, null) || ReferenceEquals(b, null))
            {
                return false;
            }

            return a.Id.Equals(b.Id);
        }

        public static bool operator !=(Node a, Node b)
        {
            return !(a == b);
        }

        public static NodeClientType RecognizeClientType(string clientId)
        {
            if (clientId is null)
            {
                return NodeClientType.Unknown;
            }
            else if (clientId.Contains(nameof(NodeClientType.Besu), StringComparison.OrdinalIgnoreCase))
            {
                return NodeClientType.Besu;
            }
            else if (clientId.Contains(nameof(NodeClientType.Geth), StringComparison.OrdinalIgnoreCase))
            {
                return NodeClientType.Geth;
            }
            else if (clientId.Contains(nameof(NodeClientType.Nethermind), StringComparison.OrdinalIgnoreCase))
            {
                return NodeClientType.Nethermind;
            }
            else if (clientId.Contains(nameof(NodeClientType.Erigon), StringComparison.OrdinalIgnoreCase))
            {
                return NodeClientType.Erigon;
            }
            else if (clientId.Contains(nameof(NodeClientType.Reth), StringComparison.OrdinalIgnoreCase))
            {
                return NodeClientType.Reth;
            }
            else if (clientId.Contains(nameof(NodeClientType.Parity), StringComparison.OrdinalIgnoreCase))
            {
                return NodeClientType.Parity;
            }
            else if (clientId.Contains(nameof(NodeClientType.OpenEthereum), StringComparison.OrdinalIgnoreCase))
            {
                return NodeClientType.OpenEthereum;
            }
            else if (clientId.Contains(nameof(NodeClientType.Trinity), StringComparison.OrdinalIgnoreCase))
            {
                return NodeClientType.Trinity;
            }
            else
            {
                return NodeClientType.Unknown;
            }
        }

        public static class Format
        {
            public const string Short = "s";
            public const string AlignedShort = "a";
            public const string Console = "c";
            public const string ENode = "e";
            public const string WithId = "f";
            public const string WithPublicKey = "p";
        }
    }
}
