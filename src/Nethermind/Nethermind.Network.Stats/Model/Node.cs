// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
    /// <remarks>
    /// Instances with the same identity can merge into one shared ENR-state group so routing replacements and
    /// in-flight packet handlers observe the same verified record, sequence high-water, and refresh request.
    /// </remarks>
    public sealed class Node : IFormattable, IEquatable<Node>
    {
        private string? _clientId;
        private string? _enodeHost;
        private string? _paddedHost;
        private string? _paddedPort;
        private EnrCacheState? _enrState;
        private int? _discoveryPort;
        private IPEndPoint? _discoveryAddress;
        private static long _nextEnrCacheStateId;

        private sealed class EnrCacheState
        {
            public long Id { get; } = Interlocked.Increment(ref _nextEnrCacheStateId);
            public Lock Sync { get; } = new();
            public EnrCacheState? Redirect;
            public EnrRecordState? RecordState;
            public ulong HighestObservedSequence;
            public ulong RequestingSequence;
        }

        private sealed class EnrRecordState(NodeRecord record, bool isVerified)
        {
            public NodeRecord Record { get; } = record;
            public bool IsVerified { get; } = isVerified;
        }

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
        public string Host => _host ??= Address.Address.ToString();
        private string? _host;

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


        public string? ClientId
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

        public string? EthDetails { get; set; }
        public long CurrentReputation { get; set; }
        public NodeRecord? Enr
        {
            get
            {
                while (true)
                {
                    EnrCacheState? state = GetEnrState();
                    if (state is null)
                    {
                        return null;
                    }

                    EnrRecordState? recordState = Volatile.Read(ref state.RecordState);
                    if (Volatile.Read(ref state.Redirect) is null)
                    {
                        return recordState?.Record;
                    }
                }
            }
            set
            {
                EnrCacheState? state = Volatile.Read(ref _enrState);
                if (state is null && value is null)
                {
                    return;
                }

                EnrRecordState? replacement = value is null ? null : new EnrRecordState(value, isVerified: false);
                while (true)
                {
                    state = GetOrCreateEnrState();
                    lock (state.Sync)
                    {
                        if (Volatile.Read(ref state.Redirect) is not null)
                        {
                            continue;
                        }

                        Volatile.Write(ref state.RecordState, replacement);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Whether <paramref name="value"/> is the ENR stored after its signature and node identity were verified.
        /// </summary>
        public bool IsVerifiedEnr(NodeRecord value)
        {
            while (true)
            {
                EnrCacheState? state = GetEnrState();
                if (state is null)
                {
                    return false;
                }

                EnrRecordState? recordState = Volatile.Read(ref state.RecordState);
                if (Volatile.Read(ref state.Redirect) is null)
                {
                    return recordState?.IsVerified == true && ReferenceEquals(recordState.Record, value);
                }
            }
        }

        /// <summary>
        /// Atomically stores an ENR whose signature and node identity have been verified by the caller,
        /// unless a higher authenticated sequence is already known.
        /// </summary>
        /// <returns><see langword="true"/> when the record was stored; otherwise <see langword="false"/>.</returns>
        public bool SetVerifiedEnr(NodeRecord value)
        {
            ArgumentNullException.ThrowIfNull(value);
            ulong sequence = value.EnrSequence;
            EnrRecordState? replacement = null;

            while (true)
            {
                EnrCacheState state = GetOrCreateEnrState();
                lock (state.Sync)
                {
                    if (Volatile.Read(ref state.Redirect) is not null)
                    {
                        continue;
                    }

                    ulong highestObservedSequence = Volatile.Read(ref state.HighestObservedSequence);
                    if (highestObservedSequence < sequence)
                    {
                        Volatile.Write(ref state.HighestObservedSequence, sequence);
                    }

                    ClearSatisfiedEnrRequest(state, sequence);
                    if (highestObservedSequence > sequence)
                    {
                        return false;
                    }

                    EnrRecordState? current = Volatile.Read(ref state.RecordState);
                    // An unverified sequence is not authenticated and cannot block a verified record.
                    if (current?.IsVerified == true)
                    {
                        if (ReferenceEquals(current.Record, value))
                        {
                            return true;
                        }

                        if (current.Record.EnrSequence >= sequence)
                        {
                            return false;
                        }
                    }

                    replacement ??= new EnrRecordState(value, isVerified: true);
                    Volatile.Write(ref state.RecordState, replacement);
                    return true;
                }
            }
        }

        /// <summary>
        /// Merges this node's ENR record, authenticated high-water mark, and request into an existing routing entry's state,
        /// then shares that state, including the highest in-flight request sequence.
        /// </summary>
        public void MergeEnrStateFrom(Node existingNode)
        {
            ValidateEnrStateSource(existingNode);
            while (true)
            {
                EnrCacheState existingState = existingNode.GetOrCreateEnrState();
                EnrCacheState? candidateState = GetEnrState();
                if (candidateState is null)
                {
                    if (Interlocked.CompareExchange(ref _enrState, existingState, null) is null)
                    {
                        return;
                    }

                    continue;
                }

                if (ReferenceEquals(candidateState, existingState))
                {
                    Volatile.Write(ref _enrState, existingState);
                    return;
                }

                EnrCacheState first = candidateState.Id < existingState.Id ? candidateState : existingState;
                EnrCacheState second = ReferenceEquals(first, candidateState) ? existingState : candidateState;
                lock (first.Sync)
                {
                    lock (second.Sync)
                    {
                        if (Volatile.Read(ref candidateState.Redirect) is not null ||
                            Volatile.Read(ref existingState.Redirect) is not null)
                        {
                            continue;
                        }

                        MergeEnrStates(candidateState, existingState);
                        Volatile.Write(ref candidateState.Redirect, existingState);
                        Volatile.Write(ref _enrState, existingState);
                        return;
                    }
                }
            }
        }

        private void ValidateEnrStateSource(Node source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!Id.Equals(source.Id))
            {
                throw new ArgumentException("ENR state can only be shared by nodes with the same identity.", nameof(source));
            }
        }

        private static void MergeEnrStates(EnrCacheState candidate, EnrCacheState existing)
        {
            ulong existingHighWater = Volatile.Read(ref existing.HighestObservedSequence);
            EnrRecordState? candidateRecord = Volatile.Read(ref candidate.RecordState);
            EnrRecordState? existingRecord = Volatile.Read(ref existing.RecordState);
            if (candidateRecord?.IsVerified == true)
            {
                ulong candidateSequence = candidateRecord.Record.EnrSequence;
                if (existingRecord?.IsVerified != true || existingRecord.Record.EnrSequence < candidateSequence)
                {
                    existingRecord = candidateRecord;
                }
            }
            else if (candidateRecord is not null &&
                     existingRecord?.IsVerified != true &&
                     (existingRecord is null || existingRecord.Record.EnrSequence < candidateRecord.Record.EnrSequence))
            {
                existingRecord = candidateRecord;
            }

            ulong highestObservedSequence = Math.Max(
                existingHighWater,
                Volatile.Read(ref candidate.HighestObservedSequence));
            ulong requestingSequence = Math.Max(
                Volatile.Read(ref existing.RequestingSequence),
                Volatile.Read(ref candidate.RequestingSequence));
            if (requestingSequence <= highestObservedSequence)
            {
                requestingSequence = 0;
            }

            Volatile.Write(ref existing.HighestObservedSequence, highestObservedSequence);
            Volatile.Write(ref existing.RecordState, existingRecord);
            Volatile.Write(ref existing.RequestingSequence, requestingSequence);
        }

        private EnrCacheState? GetEnrState()
        {
            EnrCacheState? state = Volatile.Read(ref _enrState);
            if (state is null)
            {
                return null;
            }

            EnrCacheState current = FollowEnrState(state);
            if (!ReferenceEquals(state, current))
            {
                Volatile.Write(ref _enrState, current);
            }

            return current;
        }

        private EnrCacheState GetOrCreateEnrState()
        {
            EnrCacheState? state = GetEnrState();
            if (state is not null)
            {
                return state;
            }

            EnrCacheState created = new();
            state = Interlocked.CompareExchange(ref _enrState, created, null) ?? created;
            return FollowEnrState(state);
        }

        private static EnrCacheState FollowEnrState(EnrCacheState state)
        {
            EnrCacheState? redirect;
            while ((redirect = Volatile.Read(ref state.Redirect)) is not null)
            {
                state = redirect;
            }

            return state;
        }

        /// <summary>
        /// Highest sequence of a valid Ethereum Node Record observed for this node, including records without a locally reachable endpoint.
        /// </summary>
        public ulong HighestObservedEnrSequence
        {
            get
            {
                while (true)
                {
                    EnrCacheState? state = GetEnrState();
                    if (state is null)
                    {
                        return 0;
                    }

                    ulong sequence = Volatile.Read(ref state.HighestObservedSequence);
                    if (Volatile.Read(ref state.Redirect) is null)
                    {
                        return sequence;
                    }
                }
            }
        }

        /// <summary>
        /// Highest advertised ENR sequence currently being requested for this node; <c>0</c> means no request is active.
        /// </summary>
        public ulong RequestingEnrSequence
        {
            get
            {
                while (true)
                {
                    EnrCacheState? state = GetEnrState();
                    if (state is null)
                    {
                        return 0;
                    }

                    ulong sequence = Volatile.Read(ref state.RequestingSequence);
                    if (Volatile.Read(ref state.Redirect) is null)
                    {
                        return sequence;
                    }
                }
            }
        }

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
                EnrCacheState state = GetOrCreateEnrState();
                lock (state.Sync)
                {
                    if (Volatile.Read(ref state.Redirect) is not null)
                    {
                        continue;
                    }

                    ulong current = Volatile.Read(ref state.RequestingSequence);
                    if (current >= sequence || Volatile.Read(ref state.HighestObservedSequence) >= sequence)
                    {
                        return false;
                    }

                    Volatile.Write(ref state.RequestingSequence, sequence);
                    return current == 0;
                }
            }
        }

        /// <summary>
        /// Records a valid ENR sequence as observed without requiring the record to replace the node's reachable endpoint.
        /// </summary>
        /// <param name="sequence">Sequence of the valid record that was observed.</param>
        /// <returns><see langword="true"/> when this observation completed the active request.</returns>
        public bool ObserveEnrSequence(ulong sequence)
        {
            while (true)
            {
                EnrCacheState state = GetOrCreateEnrState();
                lock (state.Sync)
                {
                    if (Volatile.Read(ref state.Redirect) is not null)
                    {
                        continue;
                    }

                    if (Volatile.Read(ref state.HighestObservedSequence) < sequence)
                    {
                        Volatile.Write(ref state.HighestObservedSequence, sequence);
                    }

                    return ClearSatisfiedEnrRequest(state, sequence);
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
                EnrCacheState? state = GetEnrState();
                if (state is null)
                {
                    return false;
                }

                lock (state.Sync)
                {
                    if (Volatile.Read(ref state.Redirect) is not null)
                    {
                        continue;
                    }

                    return ClearSatisfiedEnrRequest(state, sequence);
                }
            }
        }

        private static bool ClearSatisfiedEnrRequest(EnrCacheState state, ulong sequence)
        {
            ulong current = Volatile.Read(ref state.RequestingSequence);
            if (current == 0 || current > sequence)
            {
                return false;
            }

            Volatile.Write(ref state.RequestingSequence, 0);
            return true;
        }

        public Node(NetworkNode networkNode, bool isStatic = false)
            : this(networkNode.NodeId, GetTcpEndpoint(networkNode), isStatic)
        {
            if (networkNode.IsEnr)
            {
                SetVerifiedEnr(networkNode.Enr);
                if (networkNode.Enr.TryGetDiscoveryEndpoint(Address.AddressFamily, out IPEndPoint? discoveryEndpoint))
                {
                    DiscoveryPort = discoveryEndpoint.Port;
                }
                else
                {
                    ClearDiscoveryEndpoint();
                }
            }
            else if (networkNode.DiscoveryPort == 0)
            {
                ClearDiscoveryEndpoint();
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
        public static bool TryFromEnr(NodeRecord enr, [NotNullWhen(true)] out Node? node)
        {
            if (!enr.TryGetTcpEndpoint(out IPEndPoint? tcpEndpoint))
            {
                node = null;
                return false;
            }

            return TryFromEnr(enr, tcpEndpoint, out node);
        }

        /// <summary>
        /// Tries to create an RLPx peer candidate from an Ethereum Node Record for an address family.
        /// </summary>
        /// <param name="enr">The Ethereum Node Record to read.</param>
        /// <param name="addressFamily">The IPv4 or IPv6 address family to select.</param>
        /// <param name="node">The node created from the record when the record contains a usable TCP endpoint.</param>
        /// <returns><see langword="true"/> when a node could be created; otherwise <see langword="false"/>.</returns>
        public static bool TryFromEnr(NodeRecord enr, AddressFamily addressFamily, [NotNullWhen(true)] out Node? node)
        {
            if (!enr.TryGetTcpEndpoint(addressFamily, out IPEndPoint? tcpEndpoint))
            {
                node = null;
                return false;
            }

            return TryFromEnr(enr, tcpEndpoint, out node);
        }

        /// <summary>
        /// Tries to create a discovery-routing node from an Ethereum Node Record with a secp256k1 key and UDP endpoint.
        /// </summary>
        /// <param name="enr">The Ethereum Node Record to read.</param>
        /// <param name="node">The node created from the record when the record contains a usable UDP discovery endpoint.</param>
        /// <returns><see langword="true"/> when a node could be created; otherwise <see langword="false"/>.</returns>
        public static bool TryFromDiscoveryEnr(NodeRecord enr, [NotNullWhen(true)] out Node? node)
        {
            if (!enr.TryGetDiscoveryEndpoint(out IPEndPoint? discoveryEndpoint))
            {
                node = null;
                return false;
            }

            return TryFromDiscoveryEnr(enr, discoveryEndpoint, out node);
        }

        /// <summary>
        /// Tries to create a discovery-routing node from an Ethereum Node Record for an address family.
        /// </summary>
        /// <param name="enr">The Ethereum Node Record to read.</param>
        /// <param name="addressFamily">The IPv4 or IPv6 address family to select.</param>
        /// <param name="node">The node created from the record when the record contains a usable UDP discovery endpoint.</param>
        /// <returns><see langword="true"/> when a node could be created; otherwise <see langword="false"/>.</returns>
        public static bool TryFromDiscoveryEnr(NodeRecord enr, AddressFamily addressFamily, [NotNullWhen(true)] out Node? node)
        {
            if (!enr.TryGetDiscoveryEndpoint(addressFamily, out IPEndPoint? discoveryEndpoint))
            {
                node = null;
                return false;
            }

            return TryFromDiscoveryEnr(enr, discoveryEndpoint, out node);
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

        [MemberNotNull(nameof(Address))]
        private void SetIPEndPoint(IPEndPoint address)
        {
            Address = address.Address.IsIPv4MappedToIPv6
                ? new IPEndPoint(address.Address.MapToIPv4(), address.Port)
                : address;
            _host = null;
            _enodeHost = null;
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

            if (networkNode.Enr.TryGetTcpEndpoint(out IPEndPoint? tcpEndpoint))
            {
                return tcpEndpoint;
            }

            if (networkNode.Enr.TryGetDiscoveryEndpoint(out IPEndPoint? discoveryEndpoint))
            {
                return new IPEndPoint(discoveryEndpoint.Address, 0);
            }

            throw new InvalidOperationException("ENR is missing a usable IP endpoint.");
        }

        private static bool TryFromEnr(NodeRecord enr, IPEndPoint tcpEndpoint, [NotNullWhen(true)] out Node? node)
        {
            PublicKey? key = enr.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress();
            if (key is null)
            {
                node = null;
                return false;
            }

            node = new Node(key, tcpEndpoint)
            {
                Enr = enr
            };

            SetMatchingDiscoveryEndpoint(node, enr, tcpEndpoint.Address.AddressFamily);
            return true;
        }

        private static bool TryFromDiscoveryEnr(NodeRecord enr, IPEndPoint discoveryEndpoint, [NotNullWhen(true)] out Node? node)
        {
            PublicKey? key = enr.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress();
            if (key is null)
            {
                node = null;
                return false;
            }

            IPEndPoint tcpEndpoint = enr.TryGetTcpEndpoint(discoveryEndpoint.Address.AddressFamily, out IPEndPoint? foundTcpEndpoint)
                ? foundTcpEndpoint
                : new IPEndPoint(discoveryEndpoint.Address, 0);

            node = new Node(key, tcpEndpoint, discoveryEndpoint.Port)
            {
                Enr = enr
            };
            return true;
        }

        private static void SetMatchingDiscoveryEndpoint(Node node, NodeRecord enr, AddressFamily addressFamily)
        {
            if (enr.TryGetDiscoveryEndpoint(addressFamily, out IPEndPoint? discoveryEndpoint))
            {
                node.DiscoveryPort = discoveryEndpoint.Port;
            }
            else
            {
                node.ClearDiscoveryEndpoint();
            }
        }

        // xxx.xxx.xxx.xxx = 15
        private string PaddedHost => _paddedHost ??= Host.PadLeft(15, ' ');
        private string EnodeHost => _enodeHost ??= Enode.FormatEnodeHost(Address.Address);

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

        public override bool Equals(object? obj)
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

        public string ToString(string? format) => ToString(format, null);

        public string ToString(string? format, IFormatProvider? formatProvider) => format switch
        {
            Format.Short => $"{Host}:{Port}",
            Format.AlignedShort => $"{PaddedHost}:{PaddedPort}",
            Format.Console => $"[Node|{Host}:{Port}|{EthDetails}|{ClientId}]",
            Format.WithId => $"enode://{Id.ToString(false)}@{EnodeHost}:{Port}|{ClientId}",
            Format.ENode => $"enode://{Id.ToString(false)}@{EnodeHost}:{Port}",
            Format.WithPublicKey => $"enode://{Id.ToString(false)}@{EnodeHost}:{Port}|{Id.Address}",
            _ => $"enode://{Id.ToString(false)}@{EnodeHost}:{Port}"
        };

        public bool Equals(Node? other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;

            return Id.Equals(other.Id);
        }

        public static bool operator ==(Node? a, Node? b)
        {
            if (ReferenceEquals(a, b)) return true;

            if (a is null || b is null)
            {
                return false;
            }

            return a.Id.Equals(b.Id);
        }

        public static bool operator !=(Node? a, Node? b) => !(a == b);

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

        public static NodeClientType RecognizeClientType(string? clientId)
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
