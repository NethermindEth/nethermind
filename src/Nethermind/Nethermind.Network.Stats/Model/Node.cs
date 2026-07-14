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
        private ulong _requestingEnrSequence;
        private NodeRecord _enr;
        private EndpointState _endpoint;
        private readonly object _endpointUpdateLock = new();

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
        public string Host => Volatile.Read(ref _endpoint).Host;

        /// <summary>
        /// TCP port part of the network node.
        /// </summary>
        public int Port
        {
            get => Volatile.Read(ref _endpoint).Address.Port;
            set
            {
                lock (_endpointUpdateLock)
                {
                    EndpointState endpoint = _endpoint;
                    SetEndpoint(new EndpointState(
                        new IPEndPoint(endpoint.Address.Address, value),
                        endpoint.ExplicitDiscoveryAddress,
                        endpoint.HasDiscoveryEndpoint));
                }
            }
        }

        /// <summary>
        /// TCP network address of the node.
        /// </summary>
        public IPEndPoint Address => Volatile.Read(ref _endpoint).Address;

        /// <summary>
        /// UDP discovery port part of the network node.
        /// </summary>
        public int DiscoveryPort
        {
            get => Volatile.Read(ref _endpoint).DiscoveryAddress.Port;
            set
            {
                lock (_endpointUpdateLock)
                {
                    EndpointState endpoint = _endpoint;
                    SetEndpoint(new EndpointState(
                        endpoint.Address,
                        new IPEndPoint(endpoint.Address.Address, value),
                        hasDiscoveryEndpoint: true));
                }
            }
        }

        /// <summary>
        /// UDP discovery address of the node.
        /// </summary>
        public IPEndPoint DiscoveryAddress => Volatile.Read(ref _endpoint).DiscoveryAddress;

        /// <summary>
        /// Indicates whether the node can be used as a UDP discovery endpoint.
        /// </summary>
        public bool HasDiscoveryEndpoint => Volatile.Read(ref _endpoint).HasDiscoveryEndpoint;

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
                if (networkNode.Enr.TryGetDiscoveryEndpoint(out IPEndPoint discoveryEndpoint))
                {
                    SetDiscoveryEndpoint(discoveryEndpoint);
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
            if (key is null || !enr.TryGetTcpEndpoint(out IPEndPoint tcpEndpoint))
            {
                return false;
            }

            node = new Node(key, tcpEndpoint)
            {
                Enr = enr
            };

            SetEnrDiscoveryEndpoint(node, enr);
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
            if (key is null || !enr.TryGetDiscoveryEndpoint(out IPEndPoint discoveryEndpoint))
            {
                return false;
            }

            IPEndPoint tcpEndpoint = enr.TryGetTcpEndpoint(out IPEndPoint foundTcpEndpoint) &&
                foundTcpEndpoint.Address.Equals(discoveryEndpoint.Address)
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
            _endpoint = new EndpointState(address, explicitDiscoveryAddress: null, hasDiscoveryEndpoint: true);
        }

        public Node(PublicKey id, IPEndPoint address, int discoveryPort, bool isStatic = false)
            : this(id, address, isStatic)
            => DiscoveryPort = discoveryPort;

        /// <summary>
        /// Sets the UDP discovery endpoint independently from the TCP endpoint.
        /// </summary>
        /// <param name="endpoint">The discovery endpoint.</param>
        public void SetDiscoveryEndpoint(IPEndPoint endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            lock (_endpointUpdateLock)
            {
                SetEndpoint(new EndpointState(_endpoint.Address, endpoint, hasDiscoveryEndpoint: true));
            }
        }

        /// <summary>
        /// Updates this node's network endpoint from another instance with the same identity.
        /// </summary>
        /// <remarks>Endpoint readers observe either the complete old endpoint or the complete replacement.</remarks>
        /// <param name="node">The node containing the updated endpoint.</param>
        public void UpdateEndpoint(Node node)
        {
            ArgumentNullException.ThrowIfNull(node);
            if (!Id.Equals(node.Id))
            {
                throw new ArgumentException("A node endpoint update must keep the node identity.", nameof(node));
            }

            lock (_endpointUpdateLock)
            {
                if (IsStatic || IsTrusted || IsBootnode)
                {
                    return;
                }

                bool candidateIsConfigured = node.IsStatic || node.IsTrusted || node.IsBootnode;
                if (Enr is { } currentRecord &&
                    node.Enr is { } candidateRecord &&
                    !candidateIsConfigured &&
                    candidateRecord.EnrSequence < currentRecord.EnrSequence)
                {
                    return;
                }

                SetEndpoint(Volatile.Read(ref node._endpoint));

                if (candidateIsConfigured)
                {
                    Enr = node.Enr;
                    IsStatic |= node.IsStatic;
                    IsTrusted |= node.IsTrusted;
                    IsBootnode |= node.IsBootnode;
                }
                else if (node.Enr is { } record && (Enr is null || record.EnrSequence >= Enr.EnrSequence))
                {
                    Enr = record;
                }
            }
        }

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

        private void ClearDiscoveryEndpoint()
        {
            lock (_endpointUpdateLock)
            {
                SetEndpoint(new EndpointState(_endpoint.Address, explicitDiscoveryAddress: null, hasDiscoveryEndpoint: false));
            }
        }

        private void SetEndpoint(EndpointState endpoint) => Volatile.Write(ref _endpoint, endpoint);

        private static IPEndPoint GetTcpEndpoint(NetworkNode networkNode)
        {
            if (!networkNode.IsEnr)
            {
                return GetIPEndPoint(networkNode.Host, networkNode.Port);
            }

            if (networkNode.Enr.TryGetTcpEndpoint(out IPEndPoint tcpEndpoint))
            {
                return tcpEndpoint;
            }

            if (networkNode.Enr.TryGetDiscoveryEndpoint(out IPEndPoint discoveryEndpoint))
            {
                return new IPEndPoint(discoveryEndpoint.Address, 0);
            }

            throw new InvalidOperationException("ENR is missing a usable IP endpoint.");
        }

        private static void SetEnrDiscoveryEndpoint(Node node, NodeRecord enr)
        {
            if (enr.TryGetDiscoveryEndpoint(out IPEndPoint discoveryEndpoint))
            {
                node.SetDiscoveryEndpoint(discoveryEndpoint);
            }
            else
            {
                node.ClearDiscoveryEndpoint();
            }
        }

        private static string FormatHost(IPAddress address)
            => address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();

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

        public string ToString(string format, IFormatProvider formatProvider)
        {
            EndpointState endpoint = Volatile.Read(ref _endpoint);
            return format switch
            {
                Format.Short => $"{endpoint.Host}:{endpoint.Address.Port}",
                Format.AlignedShort => $"{endpoint.PaddedHost}:{endpoint.PaddedPort}",
                Format.Console => $"[Node|{endpoint.Host}:{endpoint.Address.Port}|{EthDetails}|{ClientId}]",
                Format.WithId => $"enode://{Id.ToString(false)}@{endpoint.Host}:{endpoint.Address.Port}|{ClientId}",
                Format.ENode => $"enode://{Id.ToString(false)}@{endpoint.Host}:{endpoint.Address.Port}",
                Format.WithPublicKey => $"enode://{Id.ToString(false)}@{endpoint.Host}:{endpoint.Address.Port}|{Id.Address}",
                _ => $"enode://{Id.ToString(false)}@{endpoint.Host}:{endpoint.Address.Port}"
            };
        }

        private sealed class EndpointState
        {
            public EndpointState(IPEndPoint address, IPEndPoint explicitDiscoveryAddress, bool hasDiscoveryEndpoint)
            {
                Address = address;
                ExplicitDiscoveryAddress = explicitDiscoveryAddress;
                HasDiscoveryEndpoint = hasDiscoveryEndpoint;
                Host = FormatHost(address.Address);
                PaddedHost = Host.PadLeft(15, ' ');
                int port = address.Port;
                PaddedPort = port >= 30300 && port <= 30399
                    ? _ports[port - 30300]
                    : port.ToString().PadLeft(5, ' ');
            }

            public IPEndPoint Address { get; }
            public IPEndPoint ExplicitDiscoveryAddress { get; }
            public IPEndPoint DiscoveryAddress => ExplicitDiscoveryAddress ?? Address;
            public bool HasDiscoveryEndpoint { get; }
            public string Host { get; }
            public string PaddedHost { get; }
            public string PaddedPort { get; }
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
