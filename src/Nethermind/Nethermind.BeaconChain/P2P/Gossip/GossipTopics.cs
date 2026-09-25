// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

    /// <summary>
    /// Gloas <c>execution_payload</c> (EIP-7732): the envelope a non-attesting follower needs.
    /// Deliberately excludes <c>execution_payload_bid</c> and <c>proposer_preferences</c>, which
    /// exist only for builders and proposers.
    /// </summary>
    public const string ExecutionPayload = "execution_payload";

    /// <summary>Gloas <c>payload_attestation_message</c> (EIP-7732).</summary>
    public const string PayloadAttestationMessage = "payload_attestation_message";

    // voluntary_exit and proposer_slashing are not subscribed: nothing consumes them and a message cannot be forwarded before full validation.
    /// <summary>The topic names the gossip router subscribes to on every digest, pre-Gloas.</summary>
    public static readonly string[] SubscribedTopicNames = [BeaconBlock, BeaconAggregateAndProof, AttesterSlashing];

    // payload_attestation_message is not subscribed until fork choice consumes PTC votes.
    /// <summary>The additional topic names that only exist from the Gloas fork onward.</summary>
    public static readonly string[] GloasTopicNames = [ExecutionPayload];

    private const string TopicPrefix = "/eth2/";
    private const string TopicSuffix = "/ssz_snappy";
    private const int DigestHexLength = 8;
    private const string DataColumnSidecarPrefix = "data_column_sidecar_";

    // The fixed global topic names of every fork up to Gloas (the p2p-interface.md "Global topics" tables).
    private static readonly HashSet<string> GlobalTopicNames =
    [
        BeaconBlock, BeaconAggregateAndProof, VoluntaryExit, ProposerSlashing, AttesterSlashing,
        "sync_committee_contribution_and_proof", "bls_to_execution_change",
        "light_client_finality_update", "light_client_optimistic_update",
        "execution_payload_bid", ExecutionPayload, PayloadAttestationMessage, "proposer_preferences",
    ];

    private static readonly string[] SubnetTopicPrefixes = ["beacon_attestation_", "sync_committee_", "blob_sidecar_", DataColumnSidecarPrefix];

    /// <summary>Builds the full topic string for a fork digest and topic name.</summary>
    public static string Topic(byte[] forkDigest, string name) => $"/eth2/{forkDigest.ToHexString()}/{name}/ssz_snappy";

    /// <summary>Builds the <c>data_column_sidecar_{subnet_id}</c> topic name for a subnet.</summary>
    public static string DataColumnSidecarTopicName(ulong subnetId) => $"{DataColumnSidecarPrefix}{subnetId}";

    /// <summary>Splits a topic string of the form <c>/eth2/{fork_digest}/{name}/ssz_snappy</c>.</summary>
    /// <returns><c>false</c> when <paramref name="topic"/> does not have that form.</returns>
    public static bool TryParse(string topic, [NotNullWhen(true)] out byte[]? forkDigest, [NotNullWhen(true)] out string? name)
    {
        forkDigest = null;
        name = null;
        if (!topic.StartsWith(TopicPrefix, StringComparison.Ordinal) || !topic.EndsWith(TopicSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> rest = topic.AsSpan(TopicPrefix.Length, topic.Length - TopicPrefix.Length - TopicSuffix.Length);
        if (rest.Length <= DigestHexLength + 1 || rest[DigestHexLength] != '/' || rest[(DigestHexLength + 1)..].Contains('/'))
        {
            return false;
        }

        byte[] digest = new byte[DigestHexLength / 2];
        if (Convert.FromHexString(rest[..DigestHexLength], digest, out _, out _) != System.Buffers.OperationStatus.Done)
        {
            return false;
        }

        forkDigest = digest;
        name = rest[(DigestHexLength + 1)..].ToString();
        return true;
    }

    /// <summary>Whether <paramref name="name"/> is a gossip topic name some fork up to Gloas defines.</summary>
    public static bool IsEth2TopicName(string name)
    {
        if (GlobalTopicNames.Contains(name))
        {
            return true;
        }

        foreach (string prefix in SubnetTopicPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && IsDecimal(name.AsSpan(prefix.Length)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="name"/> is a <c>data_column_sidecar_{subnet_id}</c> topic name.</summary>
    public static bool IsDataColumnSidecarTopicName(string name) =>
        name.StartsWith(DataColumnSidecarPrefix, StringComparison.Ordinal) && IsDecimal(name.AsSpan(DataColumnSidecarPrefix.Length));

    private static bool IsDecimal(ReadOnlySpan<char> value) => !value.IsEmpty && !value.ContainsAnyExceptInRange('0', '9');

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
