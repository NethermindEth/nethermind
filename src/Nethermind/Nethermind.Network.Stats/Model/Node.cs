// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using FastEnumUtility;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Network.Enr;

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
        private ulong _requestingEnrSequence;
        private NodeRecord _enr;
        private int? _discoveryPort;
        private IPEndPoint _discoveryAddress;

        /// <summary>
        /// Node public key - same as in enode.
        /// </summary>
        public PublicKey Id { get; }

        /// <summary>
        /// Hash of the node ID used extensively in discovery and kept here to avoid rehashing.
        /// </summary>
        public Hash256 IdHash { get; }

        /// <summary>
        /// Host part of the network node.
        /// </summary>
        public string Host => _host ??= FormatHost(Address?.Address);
        private string _host;

        /// <summary>
        /// TCP port part of the network node.
        /// </summary>
        public int Port
        {
            get => Address.Port;
            set => SetIPEndPoint(new IPEndPoint(Address.Address, value));
        }

        /// <summary>
        /// TCP network address of the node.
        /// </summary>
        public IPEndPoint Address { get; private set; }

        /// <summary>
        /// UDP discovery port part of the network node.
        /// </summary>
        public int DiscoveryPort
        {
            get => _discoveryPort ?? Port;
            set
            {
                _discoveryPort = value;
                _discoveryAddress = null;
                HasDiscoveryEndpoint = true;
            }
        }

        /// <summary>
        /// UDP discovery address of the node.
        /// </summary>
        public IPEndPoint DiscoveryAddress => DiscoveryPort == Port
            ? Address
            : _discoveryAddress ??= new IPEndPoint(Address.Address, DiscoveryPort);

        /// <summary>
        /// Indicates whether the node can be used as a UDP discovery endpoint.
        /// </summary>
        public bool HasDiscoveryEndpoint { get; private set; }

        /// <summary>
        /// We use bootnodes to bootstrap the discovery process.
        /// </summary>
        public bool IsBootnode { get; set; }

        /// <summary>
        /// We try to maintain connection with static nodes at all time.
        /// </summary>
        public bool IsStatic { get; set; }

        public bool IsTrusted { get; set; }


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
        public NodeRecord Enr
        {
            get => _enr;
            set
            {
                _enr = value;
                if (value is not null)
                {
                    TryClearEnrRequest(value.EnrSequence);
                }
            }
        }

        /// <summary>
        /// Highest advertised ENR sequence currently being requested for this node; <c>0</c> means no request is active.
        /// </summary>
        public ulong RequestingEnrSequence => Volatile.Read(ref _requestingEnrSequence);

        /// <summary>
        /// Stores the highest advertised ENR sequence that should be fetched.
        /// </summary>
        /// <param name="sequence">Advertised ENR sequence to fetch.</param>
        /// <returns><see langword="true"/> when the caller should start the refresh request.</returns>
        public bool TryRequestEnrSequence(ulong sequence)
        {
            if (sequence == 0)
            {
                return false;
            }

            while (true)
            {
                ulong current = Volatile.Read(ref _requestingEnrSequence);
                if (current >= sequence)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _requestingEnrSequence, sequence, current) == current)
                {
                    return current == 0;
                }
            }
        }

        /// <summary>
        /// Clears the in-flight ENR request when no higher sequence was advertised meanwhile.
        /// </summary>
        /// <param name="sequence">Sequence that the completed request tried to satisfy.</param>
        /// <returns><see langword="true"/> when the request state was cleared.</returns>
        public bool TryClearEnrRequest(ulong sequence)
        {
            while (true)
            {
                ulong current = Volatile.Read(ref _requestingEnrSequence);
                if (current == 0 || current > sequence)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _requestingEnrSequence, 0, current) == current)
                {
                    return true;
                }
            }
        }

        public Node(NetworkNode networkNode, bool isStatic = false)
            : this(networkNode.NodeId, GetTcpEndpoint(networkNode), isStatic)
        {
            if (networkNode.IsEnr)
            {
                Enr = networkNode.Enr;
                if (TryGetEnrPort(networkNode.Enr.DiscoveryPort, out int discoveryPort))
                {
                    DiscoveryPort = discoveryPort;
                }
                else
                {
                    ClearDiscoveryEndpoint();
                }
            }
            else if (networkNode.DiscoveryPort != networkNode.Port)
            {
                DiscoveryPort = networkNode.DiscoveryPort;
            }
        }

        /// <summary>
        /// Tries to create an RLPx peer candidate from an Ethereum Node Record with a secp256k1 key and TCP endpoint.
        /// </summary>
        /// <param name="enr">The Ethereum Node Record to read.</param>
        /// <param name="node">The node created from the record when the record contains a usable TCP endpoint.</param>
        /// <returns><see langword="true"/> when a node could be created; otherwise <see langword="false"/>.</returns>
        public static bool TryFromEnr(NodeRecord enr, [MaybeNullWhen(false)] out Node node)
        {
            node = null;
            PublicKey key = enr.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress();
            if (key is null || !TryGetEnrEndpoint(enr.Ip, enr.TcpPort, out IPEndPoint tcpEndpoint))
            {
                return false;
            }

            node = new Node(key, tcpEndpoint)
            {
                Enr = enr
            };

            if (TryGetEnrPort(enr.DiscoveryPort, out int discoveryPort))
            {
                node.DiscoveryPort = discoveryPort;
            }
            else
            {
                node.ClearDiscoveryEndpoint();
            }

            return true;
        }

        /// <summary>
        /// Tries to create a discovery-routing node from an Ethereum Node Record with a secp256k1 key and UDP endpoint.
        /// </summary>
        /// <param name="enr">The Ethereum Node Record to read.</param>
        /// <param name="node">The node created from the record when the record contains a usable UDP discovery endpoint.</param>
        /// <returns><see langword="true"/> when a node could be created; otherwise <see langword="false"/>.</returns>
        public static bool TryFromDiscoveryEnr(NodeRecord enr, [MaybeNullWhen(false)] out Node node)
        {
            node = null;
            PublicKey key = enr.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress();
            if (key is null || !TryGetEnrEndpoint(enr.Ip, enr.DiscoveryPort, out IPEndPoint discoveryEndpoint))
            {
                return false;
            }

            IPEndPoint tcpEndpoint = TryGetEnrEndpoint(enr.Ip, enr.TcpPort, out IPEndPoint foundTcpEndpoint)
                ? foundTcpEndpoint
                : new IPEndPoint(discoveryEndpoint.Address, 0);

            node = new Node(key, tcpEndpoint, discoveryEndpoint.Port)
            {
                Enr = enr
            };
            return true;
        }

        public static Node FromDiscoveryEndpoint(PublicKey id, IPEndPoint discoveryAddress)
            => new(id, new IPEndPoint(discoveryAddress.Address, 0), discoveryAddress.Port);

        private static bool TryGetEnrEndpoint(IPAddress ip, int? port, [MaybeNullWhen(false)] out IPEndPoint endpoint)
        {
            endpoint = null;
            if (ip is null || !TryGetEnrPort(port, out int validPort))
            {
                return false;
            }

            endpoint = new IPEndPoint(ip, validPort);
            return true;
        }

        private static bool TryGetEnrPort(int? port, out int validPort)
        {
            validPort = 0;
            if (port is null || port.Value == 0 || (uint)port.Value > ushort.MaxValue)
            {
                return false;
            }

            validPort = port.Value;
            return true;
        }

        public Node(PublicKey id, string host, int port, bool isStatic = false)
            : this(id, GetIPEndPoint(host, port), isStatic)
        {
        }

        public Node(PublicKey id, string host, int port, int discoveryPort, bool isStatic = false)
            : this(id, GetIPEndPoint(host, port), discoveryPort, isStatic)
        {
        }

        public Node(PublicKey id, IPEndPoint address, bool isStatic = false)
        {
            Id = id;
            IdHash = Keccak.Compute(Id.PrefixedBytes);
            IsStatic = isStatic;
            SetIPEndPoint(address);
            UseDefaultDiscoveryEndpoint();
        }

        public Node(PublicKey id, IPEndPoint address, int discoveryPort, bool isStatic = false)
            : this(id, address, isStatic)
            => DiscoveryPort = discoveryPort;

        private static readonly string[] _ports = CreateCommonPortStrings();

        private static string[] CreateCommonPortStrings()
        {
            string[] ports = new string[100];
            for (int i = 0; i < ports.Length; i++)
            {
                ports[i] = (i + 30300).ToString().PadLeft(5, ' ');
            }

            return ports;
        }

        private void SetIPEndPoint(IPEndPoint address)
        {
            Address = address;
            _host = null;
            _paddedHost = null;
            _paddedPort = null;
            _discoveryAddress = null;
        }

        private void ClearDiscoveryEndpoint()
        {
            _discoveryPort = null;
            _discoveryAddress = null;
            HasDiscoveryEndpoint = false;
        }

        private void UseDefaultDiscoveryEndpoint()
        {
            _discoveryPort = null;
            _discoveryAddress = null;
            HasDiscoveryEndpoint = true;
        }

        private static IPEndPoint GetTcpEndpoint(NetworkNode networkNode)
        {
            if (!networkNode.IsEnr)
            {
                return GetIPEndPoint(networkNode.Host, networkNode.Port);
            }

            if (TryGetEnrEndpoint(networkNode.Enr.Ip, networkNode.Enr.TcpPort, out IPEndPoint tcpEndpoint))
            {
                return tcpEndpoint;
            }

            if (TryGetEnrEndpoint(networkNode.Enr.Ip, networkNode.Enr.DiscoveryPort, out IPEndPoint discoveryEndpoint))
            {
                return new IPEndPoint(discoveryEndpoint.Address, 0);
            }

            throw new InvalidOperationException("ENR is missing a usable IP endpoint.");
        }

        private static string FormatHost(IPAddress address)
            => address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();

        // xxx.xxx.xxx.xxx = 15
        private string PaddedHost => _paddedHost ??= Host.PadLeft(15, ' ');
        private string PaddedPort
        {
            get
            {
                // Port are up to 65535 => 5 chars
                return _paddedPort ??= (Port >= 30300 && Port <= 30399) ? _ports[Port - 30300] : Port.ToString().PadLeft(5, ' ');
            }
        }

        public bool? ValidatedProtocol { get; set; }

        private static IPEndPoint GetIPEndPoint(string host, int port) => new(IPAddress.Parse(host), port);

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

        public string ToString(string format, IFormatProvider formatProvider) => format switch
        {
            Format.Short => $"{Host}:{Port}",
            Format.AlignedShort => $"{PaddedHost}:{PaddedPort}",
            Format.Console => $"[Node|{Host}:{Port}|{EthDetails}|{ClientId}]",
            Format.WithId => $"enode://{Id.ToString(false)}@{Host}:{Port}|{ClientId}",
            Format.ENode => $"enode://{Id.ToString(false)}@{Host}:{Port}",
            Format.WithPublicKey => $"enode://{Id.ToString(false)}@{Host}:{Port}|{Id.Address}",
            _ => $"enode://{Id.ToString(false)}@{Host}:{Port}"
        };

        public bool Equals(Node other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;

            return Id.Equals(other.Id);
        }

        public static bool operator ==(Node a, Node b)
        {
            if (ReferenceEquals(a, b)) return true;

            if (a is null || b is null)
            {
                return false;
            }

            return a.Id.Equals(b.Id);
        }

        public static bool operator !=(Node a, Node b) => !(a == b);

        // Dynamically generates regex pattern from NodeClientType enum values (excluding Unknown).
        // Pattern structure: (ClientName|OtherClient|...)
        // Ordered by likelihood first, with longer names before potential substrings to prevent conflicts.
        private static readonly Regex _clientTypeRegex = new(
            string.Join("|",
                // Most common clients (ordered by likelihood)
                new[]
                {
                    nameof(NodeClientType.Geth),
                    nameof(NodeClientType.Nethermind),
                    nameof(NodeClientType.Reth),
                    nameof(NodeClientType.Besu),
                    nameof(NodeClientType.Erigon),
                    nameof(NodeClientType.Nimbus),
                    nameof(NodeClientType.Ethrex),
                    nameof(NodeClientType.EthereumJS),
                    nameof(NodeClientType.OpenEthereum),
                    nameof(NodeClientType.Parity),
                }
                .Concat(
                    // Less common clients (ordered by length to prevent substring conflicts)
                    FastEnum.GetNames<NodeClientType>()
                        .Except(new[]
                        {
                            nameof(NodeClientType.Unknown),
                            nameof(NodeClientType.Geth),
                            nameof(NodeClientType.Nethermind),
                            nameof(NodeClientType.Reth),
                            nameof(NodeClientType.Besu),
                            nameof(NodeClientType.Erigon),
                            nameof(NodeClientType.Nimbus),
                            nameof(NodeClientType.Ethrex),
                            nameof(NodeClientType.EthereumJS),
                            nameof(NodeClientType.OpenEthereum),
                            nameof(NodeClientType.Parity),
                        })
                        .OrderByDescending(name => name.Length))),
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static NodeClientType RecognizeClientType(string clientId)
        {
            if (clientId is null)
            {
                return NodeClientType.Unknown;
            }

            // Use EnumerateMatches to avoid allocations - it returns ValueMatch structs
            foreach (ValueMatch match in _clientTypeRegex.EnumerateMatches(clientId))
            {
                // Get the matched text as a span to avoid allocations
                ReadOnlySpan<char> matchedText = clientId.AsSpan(match.Index, match.Length);

                // Try to parse the matched client name
                if (FastEnum.TryParse(matchedText, ignoreCase: true, out NodeClientType clientType))
                {
                    return clientType;
                }
            }

            return NodeClientType.Unknown;
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
