// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using Nethermind.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

internal static class DiscoveryKademliaConfigFactory
{
    public static KademliaConfig<Node> Create(Node currentNode, IReadOnlyList<Node> bootNodes, IDiscoveryConfig discoveryConfig)
        => new()
        {
            CurrentNodeId = currentNode,
            KSize = discoveryConfig.BucketSize,
            Alpha = discoveryConfig.Concurrency,
            Beta = discoveryConfig.BitsPerHop,
            LookupFindNeighbourHardTimeout = TimeSpan.FromMilliseconds((3L * discoveryConfig.PingTimeout) + (2L * discoveryConfig.BondWaitTime) + discoveryConfig.EnrTimeout + (2L * discoveryConfig.SendNodeTimeout)),
            RefreshPingTimeout = TimeSpan.FromMilliseconds(discoveryConfig.PingTimeout),
            RefreshInterval = TimeSpan.FromMilliseconds(discoveryConfig.DiscoveryInterval),
            BootNodes = bootNodes,
            AreSameEndpoint = static (first, second) => first.DiscoveryAddress.Equals(second.DiscoveryAddress),
            RequireReplacementValidation = true,
            GetSubnet = node => node.Id.Equals(currentNode.Id) ? null : GetSubnet(node),
            IsVerified = static node => node.ValidatedProtocol is not null,
            MergeOnRefresh = static (incoming, existing) =>
            {
                if (!ReferenceEquals(incoming, existing))
                {
                    if (existing.Enr is { } retained && existing.IsVerifiedEnr(retained)
                        && incoming.Enr is { } candidate && incoming.IsVerifiedEnr(candidate)
                        && candidate.EnrSequence < retained.EnrSequence)
                    {
                        existing.IsStatic |= incoming.IsStatic;
                        existing.IsTrusted |= incoming.IsTrusted;
                        existing.IsBootnode |= incoming.IsBootnode;
                        existing.MergeEnrStateFrom(incoming);
                        return existing;
                    }
                    incoming.IsStatic |= existing.IsStatic;
                    incoming.IsTrusted |= existing.IsTrusted;
                    incoming.IsBootnode |= existing.IsBootnode;
                    incoming.MergeEnrStateFrom(existing);
                    if (incoming.DiscoveryAddress.Equals(existing.DiscoveryAddress))
                    {
                        incoming.ValidatedProtocol ??= existing.ValidatedProtocol;
                        DiscoveryLiveness.CarryProof(incoming, existing);
                    }
                }

                return incoming;
            }
        };

    private static UInt128? GetSubnet(Node node)
    {
        IPAddress address = node.DiscoveryAddress.Address;
        if (node.IsStatic || node.IsBootnode || address.IsLoopbackOrPrivateOrLinkLocal) return null;
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out int length);
        return length == 4
            ? ((UInt128)0xffff << 32) | (BinaryPrimitives.ReadUInt32BigEndian(bytes) & 0xffffff00u)
            : ((UInt128)(BinaryPrimitives.ReadUInt64BigEndian(bytes) >> 16)) << 80;
    }
}
