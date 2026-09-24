// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.DataAvailability;

/// <summary>
/// Gloas <c>compute_max_data_column_sidecar_size</c>: the bound must equal the serialized size of a
/// sidecar carrying the schedule's largest blob count, as the spec defines it.
/// </summary>
public class DataColumnSidecarGloasSizeTests
{
    private static IEnumerable<TestCaseData> ShippedSpecs()
    {
        yield return new TestCaseData(BeaconChainSpec.Mainnet).SetArgDisplayNames("mainnet");
        yield return new TestCaseData(BeaconChainSpec.Sepolia).SetArgDisplayNames("sepolia");
        yield return new TestCaseData(BeaconChainSpec.Hoodi).SetArgDisplayNames("hoodi");
    }

    /// <summary>Every shipped network tops out at 21 blobs (BPO2).</summary>
    [TestCaseSource(nameof(ShippedSpecs))]
    public void Shipped_networks_bound_a_sidecar_at_its_serialized_size_with_21_blobs(BeaconChainSpec spec)
    {
        ulong bound = DataColumnSidecarGloasSize.ComputeMax(spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bound, Is.EqualTo(44072UL));
            Assert.That(bound, Is.EqualTo((ulong)SerializedLength(21)), "the bound is the spec's len(ssz_serialize(sidecar)), not a separate formula");
        }
    }

    /// <summary>
    /// The largest entry wins wherever it sits in the schedule, and the Electra maximum counts even
    /// with no schedule. 4096 blobs reproduces the fixed <c>MAX_DATA_COLUMN_SIDECAR_SIZE</c> (8585272)
    /// that this function replaced, cross-checking the fixed part against an independent value.
    /// </summary>
    [TestCase(9UL, new ulong[0], 9)]
    [TestCase(9UL, new ulong[] { 30, 12 }, 30)]
    [TestCase(9UL, new ulong[] { 12, 30, 15 }, 30)]
    [TestCase(40UL, new ulong[] { 12, 30 }, 40)]
    [TestCase(9UL, new ulong[] { 4096 }, 4096)]
    public void The_bound_uses_the_largest_blob_count_in_the_whole_schedule(ulong electraMaxBlobs, ulong[] scheduledMaxBlobs, int expectedBlobs)
    {
        BlobScheduleEntry[] schedule = new BlobScheduleEntry[scheduledMaxBlobs.Length];
        for (int i = 0; i < schedule.Length; i++)
        {
            schedule[i] = new BlobScheduleEntry((ulong)(i + 1) * 100, scheduledMaxBlobs[i]);
        }

        ulong bound = DataColumnSidecarGloasSize.ComputeMax(SpecWith(schedule, electraMaxBlobs));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bound, Is.EqualTo((ulong)SerializedLength(expectedBlobs)));
            if (expectedBlobs == 4096) Assert.That(bound, Is.EqualTo(8585272UL));
        }
    }

    private static int SerializedLength(int blobs) => DataColumnSidecarGloas.Encode(new DataColumnSidecarGloas
    {
        Index = 0,
        Column = new SszBlobCell[blobs],
        KzgProofs = new SszKzgCommitment[blobs],
        Slot = 0,
        BeaconBlockRoot = Hash256.Zero,
    }).Length;

    private static BeaconChainSpec SpecWith(BlobScheduleEntry[] schedule, ulong electraMaxBlobs)
    {
        BeaconChainSpec mainnet = BeaconChainSpec.Mainnet;
        return new BeaconChainSpec
        {
            SecondsPerSlot = mainnet.SecondsPerSlot,
            SlotsPerEpoch = mainnet.SlotsPerEpoch,
            GenesisTime = mainnet.GenesisTime,
            GenesisValidatorsRoot = mainnet.GenesisValidatorsRoot,
            Forks = mainnet.Forks,
            BlobSchedule = schedule,
            ElectraForkEpoch = mainnet.ElectraForkEpoch,
            FuluForkEpoch = mainnet.FuluForkEpoch,
            MaxBlobsPerBlockElectra = electraMaxBlobs,
            GloasForkEpoch = mainnet.GloasForkEpoch,
            GloasForkVersion = mainnet.GloasForkVersion,
            Bootnodes = mainnet.Bootnodes,
        };
    }
}
