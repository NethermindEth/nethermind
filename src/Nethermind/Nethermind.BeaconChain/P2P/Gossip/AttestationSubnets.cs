// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>Joins the <c>beacon_attestation_{subnet_id}</c> topics local aggregators collect from, for the slots they aggregate in, and advertises them.</summary>
/// <remarks>
/// <para>
/// phase0/validator.md "Lookahead": an aggregator subscribes to its committee's subnet, while an attester that does not aggregate only
/// publishes there, by fanout. A subnet is joined <see cref="JoinLeadSlots"/> before the first aggregation slot that needs it, so its mesh
/// forms before the attestations are sent, and left once that slot has passed. The long-lived subnets of phase0/p2p-interface.md
/// "Attestation subnet subscription" are not joined.
/// </para>
/// <para>
/// The joined subnets are advertised in <c>MetaData.attnets</c>, which takes a new sequence number on every change, and in the ENR
/// <c>attnets</c> entry, which the spec requires to match it while any bit is set.
/// </para>
/// <para>Thread safety: every member may be called from any thread and concurrently.</para>
/// </remarks>
public sealed class AttestationSubnets(GossipRouter gossip, LocalMetadataSource metadata, BeaconDiscovery discovery, ILogManager logManager)
{
    /// <summary>The slots before an aggregation slot at which its subnet is joined: enough heartbeats for the mesh to graft.</summary>
    internal const ulong JoinLeadSlots = 2;

    private readonly ILogger _logger = logManager.GetClassLogger<AttestationSubnets>();
    private readonly Lock _lock = new();
    private readonly HashSet<(ulong Subnet, ulong Slot)> _aggregations = [];
    private ulong _joined;

    /// <summary>The subnets joined now, one bit each, subnet 0 lowest.</summary>
    internal ulong Joined
    {
        get
        {
            lock (_lock) return _joined;
        }
    }

    /// <summary>Records that a local aggregator collects the attestations of <paramref name="subnet"/> in <paramref name="slot"/>.</summary>
    /// <remarks>The caller bounds <paramref name="slot"/>, so at most one entry for each subnet and slot of the lookahead is held.</remarks>
    internal void Add(ulong subnet, ulong slot)
    {
        lock (_lock) _aggregations.Add((subnet, slot));
    }

    /// <summary>Joins the subnets an aggregation within <see cref="JoinLeadSlots"/> of <paramref name="currentSlot"/> needs, leaves the others, and advertises the change.</summary>
    internal void Update(ulong currentSlot)
    {
        lock (_lock)
        {
            ulong wanted = 0;
            _aggregations.RemoveWhere(aggregation => aggregation.Slot < currentSlot);
            foreach ((ulong subnet, ulong slot) in _aggregations)
            {
                if (slot <= currentSlot + JoinLeadSlots) wanted |= 1UL << (int)subnet;
            }

            if (wanted == _joined) return;
            _joined = wanted;
            gossip.SetAttestationSubnets(wanted);
            BitArray attnets = new(BitConverter.GetBytes(wanted));
            metadata.SetAttnets(attnets);
            discovery.UpdateLocalAttnets(attnets);
            if (_logger.IsDebug) _logger.Debug($"Joined attestation subnets 0x{wanted:x16} at slot {currentSlot}");
        }
    }
}
