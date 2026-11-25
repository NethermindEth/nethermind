// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Types;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>Joins the <c>sync_committee_{subnet_id}</c> topics of the local validators in a sync committee, until their subscriptions end, and advertises them.</summary>
/// <remarks>
/// <para>
/// A subnet is joined as soon as a subscription names it and left at the subscription's <c>until_epoch</c>; when the validator client
/// subscribes ahead of a period is its choice (altair/validator.md "Sync committee subnet stability"). Joining any subnet also joins
/// <c>sync_committee_contribution_and_proof</c> (<see cref="GossipRouter.SetSyncCommitteeSubnets"/>).
/// </para>
/// <para>
/// The joined subnets are advertised in <c>MetaData.syncnets</c>, which takes a new sequence number on every change, and in the ENR
/// <c>syncnets</c> entry. Unlike attestation subnets these are long-lived by definition, so the spec has a node advertise every one its
/// validators are assigned to.
/// </para>
/// <para>Thread safety: every member may be called from any thread and concurrently.</para>
/// </remarks>
public sealed class SyncCommitteeSubnets(GossipRouter gossip, LocalMetadataSource metadata, BeaconDiscovery discovery, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<SyncCommitteeSubnets>();
    private readonly Lock _lock = new();

    // The first epoch each subnet is no longer needed in; zero when no subscription named it.
    private readonly ulong[] _untilEpochs = new ulong[SyncCommitteeContribution.SubnetCount];
    private ulong _joined;

    /// <summary>The subnets joined now, one bit each, subnet 0 lowest.</summary>
    internal ulong Joined
    {
        get
        {
            lock (_lock) return _joined;
        }
    }

    /// <summary>Records that a local validator needs <paramref name="subnet"/> until <paramref name="untilEpoch"/>, exclusive.</summary>
    /// <remarks>At most one entry is held for each subnet, the latest end asked for.</remarks>
    internal void Add(ulong subnet, ulong untilEpoch)
    {
        lock (_lock) _untilEpochs[subnet] = Math.Max(_untilEpochs[subnet], untilEpoch);
    }

    /// <summary>Joins the subnets a subscription still needs at <paramref name="currentEpoch"/>, leaves the others, and advertises the change.</summary>
    internal void Update(ulong currentEpoch)
    {
        lock (_lock)
        {
            ulong wanted = 0;
            for (int subnet = 0; subnet < _untilEpochs.Length; subnet++)
            {
                if (_untilEpochs[subnet] > currentEpoch) wanted |= 1UL << subnet;
            }

            if (wanted == _joined) return;
            _joined = wanted;
            gossip.SetSyncCommitteeSubnets(wanted);
            BitArray syncnets = new(BitConverter.GetBytes(wanted)) { Length = SyncCommitteeContribution.SubnetCount };
            metadata.SetSyncnets(syncnets);
            discovery.UpdateLocalSyncnets(syncnets);
            if (_logger.IsDebug) _logger.Debug($"Joined sync committee subnets 0x{wanted:x} at epoch {currentEpoch}");
        }
    }
}
