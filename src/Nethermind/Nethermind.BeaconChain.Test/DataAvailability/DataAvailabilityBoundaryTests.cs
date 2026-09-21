// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.DataAvailability;

/// <summary>
/// The Fulu p2p serve range's lower edge, <c>max(current_epoch - MIN_EPOCHS_FOR_DATA_COLUMN_SIDECARS_REQUESTS, FULU_FORK_EPOCH)</c>:
/// a boundary that drifts by one epoch either way silently admits window-age blocks without their
/// data or demands columns no peer is obliged to serve.
/// </summary>
public class DataAvailabilityBoundaryTests
{
    private const ulong Window = Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests;

    [TestCase(0ul, 0ul, 0ul, TestName = "an_unfilled_window_saturates_at_epoch_zero")]
    [TestCase(Window - 1, 0ul, 0ul, TestName = "one_epoch_short_of_a_full_window_still_saturates")]
    [TestCase(Window, 0ul, 0ul, TestName = "a_full_window_starts_at_epoch_zero")]
    [TestCase(Window + 1, 0ul, 1ul, TestName = "the_window_start_advances_with_the_clock")]
    [TestCase(Window + 250, 0ul, 250ul, TestName = "the_window_is_exactly_the_constant_wide")]
    [TestCase(Window + 250, 300ul, 300ul, TestName = "the_fulu_fork_epoch_floors_a_window_that_starts_earlier")]
    [TestCase(Window + 300, 300ul, 300ul, TestName = "the_window_start_and_the_fork_epoch_coincide")]
    [TestCase(Window + 301, 300ul, 301ul, TestName = "past_the_fork_the_window_start_wins")]
    public void Compute_is_the_window_start_floored_at_the_fulu_fork_epoch(ulong currentEpoch, ulong fuluForkEpoch, ulong expected) =>
        Assert.That(DataAvailabilityBoundary.Compute(currentEpoch, SpecWithFulu(fuluForkEpoch)), Is.EqualTo(expected));

    [Test]
    public void On_mainnet_the_boundary_never_precedes_the_fulu_fork()
    {
        BeaconChainSpec mainnet = BeaconChainSpec.Mainnet;

        Assert.Multiple(() =>
        {
            Assert.That(DataAvailabilityBoundary.Compute(0, mainnet), Is.EqualTo(mainnet.FuluForkEpoch));
            Assert.That(DataAvailabilityBoundary.Compute(mainnet.FuluForkEpoch + Window, mainnet), Is.EqualTo(mainnet.FuluForkEpoch));
            Assert.That(DataAvailabilityBoundary.Compute(mainnet.FuluForkEpoch + Window + 1, mainnet), Is.EqualTo(mainnet.FuluForkEpoch + 1));
        });
    }

    private static BeaconChainSpec SpecWithFulu(ulong fuluForkEpoch) => new()
    {
        SecondsPerSlot = 12,
        SlotsPerEpoch = 32,
        GenesisTime = 0,
        GenesisValidatorsRoot = Hash256.Zero,
        Forks = [],
        BlobSchedule = [],
        ElectraForkEpoch = 0,
        FuluForkEpoch = fuluForkEpoch,
        MaxBlobsPerBlockElectra = 9,
        GloasForkEpoch = Presets.FarFutureEpoch,
        GloasForkVersion = [],
        Bootnodes = [],
    };
}
