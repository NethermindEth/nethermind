// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Test.Spec;

public class GloasForkScheduleTests
{
    private const ulong GloasEpoch = 500_000ul;
    private static readonly byte[] GloasVersion = Bytes.FromHexString("0x07000000");

    private static BeaconChainSpec SyntheticGloasSpec() => BeaconChainSpec.Mainnet with
    {
        CheckpointSyncUrl = null,
        ChainId = 0,
        Forks = [.. BeaconChainSpec.Mainnet.Forks, new ForkScheduleEntry(GloasVersion, GloasEpoch)],
        GloasForkEpoch = GloasEpoch,
        GloasForkVersion = GloasVersion,
        Bootnodes = [],
    };

    [TestCase(GloasEpoch - 1, BeaconFork.Fulu)]
    [TestCase(GloasEpoch, BeaconFork.Gloas)]
    [TestCase(GloasEpoch + 1_000, BeaconFork.Gloas)]
    public void ForkAtEpoch_selects_the_expected_fork_around_the_gloas_epoch(ulong epoch, BeaconFork expected) =>
        Assert.That(SyntheticGloasSpec().ForkAtEpoch(epoch), Is.EqualTo(expected));

    [Test]
    public void ForkAtEpoch_agrees_at_the_exact_boundary_slot()
    {
        BeaconChainSpec spec = SyntheticGloasSpec();
        ulong boundarySlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(GloasEpoch);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(spec.ForkAtEpoch(spec.GetEpoch(boundarySlot - 1)), Is.EqualTo(BeaconFork.Fulu),
            "the last slot of the pre-fork epoch must still resolve to Fulu");
        Assert.That(spec.ForkAtEpoch(spec.GetEpoch(boundarySlot)), Is.EqualTo(BeaconFork.Gloas),
            "the first slot of GLOAS_FORK_EPOCH must resolve to Gloas");
    }

    [Test]
    public void ForkAtEpoch_fails_loudly_for_an_epoch_this_driver_cannot_represent() =>
        Assert.Throws<BeaconStateException>(() => SyntheticGloasSpec().ForkAtEpoch(0));

    [Test]
    public void ForkAtEpoch_on_mainnet_never_reaches_gloas_since_it_is_unscheduled() =>
        Assert.That(BeaconChainSpec.Mainnet.ForkAtEpoch(10_000_000), Is.EqualTo(BeaconFork.Fulu),
            "GloasForkEpoch is the far-future sentinel until a real date is confirmed");

    // Sepolia: ethereum/pm#2205, lodestar#10119. Hoodi: eth-clients/hoodi#35.
    [TestCase("Sepolia", 353024ul)]
    [TestCase("Hoodi", 143616ul)]
    public void ForkAtEpoch_selects_gloas_at_the_confirmed_epoch_and_fulu_immediately_before_it(string network, ulong gloasEpoch)
    {
        BeaconChainSpec spec = network == "Hoodi" ? BeaconChainSpec.Hoodi : BeaconChainSpec.Sepolia;
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(spec.ForkAtEpoch(gloasEpoch), Is.EqualTo(BeaconFork.Gloas));
        Assert.That(spec.ForkAtEpoch(gloasEpoch - 1), Is.EqualTo(BeaconFork.Fulu));
    }

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

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(gloasDigest, Is.Not.EqualTo(fuluDigest),
            "a node computing the same digest across the boundary would silently keep talking Fulu's fork id");
        Assert.That(gloasDigest, Is.EqualTo(Bytes.FromHexString("0xce2153ed")));
    }

    // Expected digests reproduced independently in Python: sha256(fork_version ++ 28 zero bytes ++
    // genesis_validators_root)[:4], XOR-masked per EIP-7892 with sha256(le64(blob_epoch) ++
    // le64(max_blobs_per_block))[:4] using the network's own confirmed genesis validators root and its
    // published BPO2 blob params (Sepolia 275712, Hoodi 54016; 21 blobs each), which are still the params in effect at the Gloas
    // boundary since BPO and hard-fork rotation are orthogonal (see ForkDigest's own remarks).
    [TestCase("Sepolia", 353024ul, "0x74d01459", "0x669e6c11")]
    [TestCase("Hoodi", 143616ul, "0xc6ecb76c", "0x5ad30129")]
    public void Fork_digest_rotates_at_the_gloas_boundary_and_matches_an_independent_computation(string network, ulong gloasEpoch, string expectedFuluDigest, string expectedGloasDigest)
    {
        BeaconChainSpec spec = network == "Hoodi" ? BeaconChainSpec.Hoodi : BeaconChainSpec.Sepolia;
        byte[] fuluDigest = ForkDigest.Compute(spec, gloasEpoch - 1);
        byte[] gloasDigest = ForkDigest.Compute(spec, gloasEpoch);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fuluDigest, Is.EqualTo(Bytes.FromHexString(expectedFuluDigest)));
        Assert.That(gloasDigest, Is.Not.EqualTo(fuluDigest),
            "a node computing the same digest across the boundary would silently keep talking Fulu's fork id");
        Assert.That(gloasDigest, Is.EqualTo(Bytes.FromHexString(expectedGloasDigest)));
    }

    // ForkAtEpoch reads scalar epochs; VersionForEpoch reads Forks. They must agree or signatures and advertised digests diverge.
    [TestCaseSource(nameof(ShippedSpecs))]
    public void Scalar_fork_epochs_match_the_fork_schedule(string name, BeaconChainSpec spec)
    {
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(spec.Forks.Any(f => f.Epoch == spec.ElectraForkEpoch), Is.True,
            $"{name}: no Forks entry at ElectraForkEpoch {spec.ElectraForkEpoch}");
        Assert.That(spec.Forks.Any(f => f.Epoch == spec.FuluForkEpoch), Is.True,
            $"{name}: no Forks entry at FuluForkEpoch {spec.FuluForkEpoch}");

        if (spec.GloasForkEpoch != Presets.FarFutureEpoch)
        {
            Assert.That(spec.Forks.Any(f => f.Epoch == spec.GloasForkEpoch), Is.True,
                $"{name}: no Forks entry at GloasForkEpoch {spec.GloasForkEpoch}");
            Assert.That(spec.GloasForkVersion,
                Is.EqualTo(spec.Forks.Single(f => f.Epoch == spec.GloasForkEpoch).Version),
                $"{name}: GloasForkVersion disagrees with the Forks entry at GloasForkEpoch");
        }
    }

    private static IEnumerable<object[]> ShippedSpecs() =>
    [
        ["Mainnet", BeaconChainSpec.Mainnet],
        ["Hoodi", BeaconChainSpec.Hoodi],
        ["Sepolia", BeaconChainSpec.Sepolia],
        ["Gnosis", BeaconChainSpec.Gnosis],
        ["Chiado", BeaconChainSpec.Chiado],
        ["SyntheticGloas", SyntheticGloasSpec()],
    ];

    [TestCase(false, "7d5aab40", "3237dab6")]
    [TestCase(true, "ff15abe1", "25f9148e")]
    public void Gnosis_network_fork_digests_match_published_configurations(bool chiado, string electra, string fulu)
    {
        BeaconChainSpec spec = chiado ? BeaconChainSpec.Chiado : BeaconChainSpec.Gnosis;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ForkDigest.Compute(spec, spec.ElectraForkEpoch), Is.EqualTo(Bytes.FromHexString(electra)));
            Assert.That(ForkDigest.Compute(spec, spec.FuluForkEpoch), Is.EqualTo(Bytes.FromHexString(fulu)));
        }
    }
}
