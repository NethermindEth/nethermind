// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>Eth2 gossipsub topic strings and the fork-digest rotation schedule.</summary>
/// <remarks>
/// Topics follow the consensus p2p spec form <c>/eth2/{fork_digest}/{name}/ssz_snappy</c>. The
/// digest rotates at every scheduled fork activation and, from Fulu onward, at every EIP-7892
/// blob-parameter-only (BPO) boundary (see <see cref="ForkDigest"/>).
/// </remarks>
public static class GossipTopics
{
    public const string BeaconBlock = "beacon_block";
    public const string BeaconAggregateAndProof = "beacon_aggregate_and_proof";
    public const string VoluntaryExit = "voluntary_exit";
    public const string ProposerSlashing = "proposer_slashing";
    public const string AttesterSlashing = "attester_slashing";

    /// <summary>The topic names the gossip router subscribes to on every digest.</summary>
    public static readonly string[] SubscribedTopicNames =
        [BeaconBlock, BeaconAggregateAndProof, VoluntaryExit, ProposerSlashing, AttesterSlashing];

    /// <summary>Builds the full topic string for a fork digest and topic name.</summary>
    public static string Topic(byte[] forkDigest, string name) => $"/eth2/{forkDigest.ToHexString()}/{name}/ssz_snappy";

    /// <summary>The fork digest in effect at <paramref name="epoch"/>.</summary>
    public static byte[] CurrentDigest(BeaconChainSpec spec, ulong epoch) => ForkDigest.Compute(spec, epoch);

    /// <summary>
    /// Epochs at or after <paramref name="fromEpoch"/> at which the fork digest changes: scheduled
    /// fork activations plus EIP-7892 BPO boundaries (which only exist from Fulu onward).
    /// </summary>
    /// <returns>Distinct rotation epochs in ascending order.</returns>
    public static IEnumerable<ulong> DigestRotationEpochs(BeaconChainSpec spec, ulong fromEpoch)
    {
        SortedSet<ulong> epochs = [];
        foreach (ForkScheduleEntry fork in spec.Forks)
        {
            if (fork.Epoch >= fromEpoch)
            {
                epochs.Add(fork.Epoch);
            }
        }

        foreach (BlobScheduleEntry blob in spec.BlobSchedule)
        {
            if (blob.Epoch >= fromEpoch && blob.Epoch >= spec.FuluForkEpoch)
            {
                epochs.Add(blob.Epoch);
            }
        }

        return epochs;
    }

    /// <summary>The next digest rotation strictly after <paramref name="epoch"/>, or <c>null</c> when none is scheduled.</summary>
    public static (ulong Epoch, byte[] Digest)? NextRotation(BeaconChainSpec spec, ulong epoch)
    {
        foreach (ulong rotationEpoch in DigestRotationEpochs(spec, epoch + 1))
        {
            return (rotationEpoch, ForkDigest.Compute(spec, rotationEpoch));
        }

        return null;
    }
}
