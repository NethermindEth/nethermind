// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Spec;

/// <summary>
/// Neither <see cref="BeaconChainSpec.Mainnet"/> nor <see cref="BeaconChainSpec.Hoodi"/> has a
/// confirmed Gloas epoch as of 2026-09-19 (see the remarks on <see cref="BeaconChainSpec.GloasForkEpoch"/>),
/// so these tests build a synthetic network - Mainnet's own values with a Gloas entry spliced into the
/// schedule - rather than assert against a guessed production epoch.
/// </summary>
public class GloasForkScheduleTests
{
    private const ulong GloasEpoch = 500_000ul;
    private static readonly byte[] GloasVersion = Bytes.FromHexString("0x07000000");

    private static BeaconChainSpec SyntheticGloasSpec() => new()
    {
        ChainId = 0,
        SecondsPerSlot = BeaconChainSpec.Mainnet.SecondsPerSlot,
        SlotsPerEpoch = BeaconChainSpec.Mainnet.SlotsPerEpoch,
        GenesisTime = BeaconChainSpec.Mainnet.GenesisTime,
        GenesisValidatorsRoot = BeaconChainSpec.Mainnet.GenesisValidatorsRoot,
        Forks = [.. BeaconChainSpec.Mainnet.Forks, new ForkScheduleEntry(GloasVersion, GloasEpoch)],
        BlobSchedule = BeaconChainSpec.Mainnet.BlobSchedule,
        ElectraForkEpoch = BeaconChainSpec.Mainnet.ElectraForkEpoch,
        FuluForkEpoch = BeaconChainSpec.Mainnet.FuluForkEpoch,
        MaxBlobsPerBlockElectra = BeaconChainSpec.Mainnet.MaxBlobsPerBlockElectra,
        GloasForkEpoch = GloasEpoch,
        GloasForkVersion = GloasVersion,
    };

    [Test]
    public void ForkAtEpoch_selects_fulu_immediately_before_the_gloas_epoch() =>
        Assert.That(SyntheticGloasSpec().ForkAtEpoch(GloasEpoch - 1), Is.EqualTo(BeaconFork.Fulu));

    [Test]
    public void ForkAtEpoch_selects_gloas_at_the_gloas_epoch() =>
        Assert.That(SyntheticGloasSpec().ForkAtEpoch(GloasEpoch), Is.EqualTo(BeaconFork.Gloas));

    [Test]
    public void ForkAtEpoch_selects_gloas_after_the_gloas_epoch() =>
        Assert.That(SyntheticGloasSpec().ForkAtEpoch(GloasEpoch + 1_000), Is.EqualTo(BeaconFork.Gloas));

    [Test]
    public void ForkAtEpoch_agrees_at_the_exact_boundary_slot()
    {
        BeaconChainSpec spec = SyntheticGloasSpec();
        ulong boundarySlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(GloasEpoch);

        Assert.Multiple(() =>
        {
            Assert.That(spec.ForkAtEpoch(spec.GetEpoch(boundarySlot - 1)), Is.EqualTo(BeaconFork.Fulu),
                "the last slot of the pre-fork epoch must still resolve to Fulu");
            Assert.That(spec.ForkAtEpoch(spec.GetEpoch(boundarySlot)), Is.EqualTo(BeaconFork.Gloas),
                "the first slot of GLOAS_FORK_EPOCH must resolve to Gloas");
        });
    }

    [Test]
    public void ForkAtEpoch_fails_loudly_for_an_epoch_this_driver_cannot_represent() =>
        Assert.Throws<BeaconStateException>(() => SyntheticGloasSpec().ForkAtEpoch(0));

    [Test]
    public void ForkAtEpoch_on_mainnet_and_hoodi_never_reaches_gloas_since_it_is_unscheduled() =>
        Assert.Multiple(() =>
        {
            Assert.That(BeaconChainSpec.Mainnet.ForkAtEpoch(10_000_000), Is.EqualTo(BeaconFork.Fulu),
                "GloasForkEpoch is the far-future sentinel until a real date is confirmed");
            Assert.That(BeaconChainSpec.Hoodi.ForkAtEpoch(10_000_000), Is.EqualTo(BeaconFork.Fulu));
        });

    // Expected digest reproduced independently in Python: sha256(fork_version ++ 28 zero bytes ++
    // genesis_validators_root)[:4], XOR-masked per EIP-7892 with sha256(le64(419072) ++ le64(21))[:4]
    // (mainnet's own BPO2 blob params, the last scheduled one, still in effect past this synthetic
    // Gloas epoch since BPO and hard-fork rotation are orthogonal - see ForkDigest's own remarks).
    [Test]
    public void Fork_digest_rotates_at_the_gloas_boundary_and_matches_an_independent_computation()
    {
        BeaconChainSpec spec = SyntheticGloasSpec();
        byte[] fuluDigest = ForkDigest.Compute(spec, GloasEpoch - 1);
        byte[] gloasDigest = ForkDigest.Compute(spec, GloasEpoch);

        Assert.Multiple(() =>
        {
            Assert.That(gloasDigest, Is.Not.EqualTo(fuluDigest),
                "a node computing the same digest across the boundary would silently keep talking Fulu's fork id");
            Assert.That(gloasDigest, Is.EqualTo(Bytes.FromHexString("0xce2153ed")));
        });
    }

    /// <summary>
    /// The scalar fork epochs and the Forks schedule are two independent sources of truth:
    /// ForkAtEpoch reads the former, VersionForEpoch (and so the fork digest, and so the node
    /// record) reads the latter. A shipped network whose two disagree would compute a digest for
    /// one fork while processing state as another and silently lose every peer at the boundary.
    /// </summary>
    [TestCaseSource(nameof(ShippedSpecs))]
    public void Scalar_fork_epochs_match_the_fork_schedule(string name, BeaconChainSpec spec) =>
        Assert.Multiple(() =>
        {
            Assert.That(spec.Forks.Any(f => f.Epoch == spec.ElectraForkEpoch), Is.True,
                $"{name}: no Forks entry at ElectraForkEpoch {spec.ElectraForkEpoch}");
            Assert.That(spec.Forks.Any(f => f.Epoch == spec.FuluForkEpoch), Is.True,
                $"{name}: no Forks entry at FuluForkEpoch {spec.FuluForkEpoch}");

            if (spec.GloasForkEpoch != Presets.FarFutureEpoch)
            {
                Assert.That(spec.Forks.Any(f => f.Epoch == spec.GloasForkEpoch), Is.True,
                    $"{name}: no Forks entry at GloasForkEpoch {spec.GloasForkEpoch}");
            }

            // The version the digest uses at a fork epoch must be the version that fork introduced.
            Assert.That(spec.VersionForEpoch(spec.FuluForkEpoch),
                Is.EqualTo(spec.Forks.Last(f => f.Epoch <= spec.FuluForkEpoch).Version),
                $"{name}: digest version at the Fulu epoch does not come from the Fulu entry");
        });

    private static IEnumerable<object[]> ShippedSpecs() =>
    [
        ["Mainnet", BeaconChainSpec.Mainnet],
        ["Hoodi", BeaconChainSpec.Hoodi],
        ["SyntheticGloas", SyntheticGloasSpec()],
    ];
}
