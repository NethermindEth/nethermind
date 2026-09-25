// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Simulate;

[TestFixture]
public class SimulateBridgeHelperTests
{
    private const ulong MainnetBeaconGenesis = MainnetSpecProvider.BeaconChainGenesisTimestampConst;
    private const ulong GnosisBeaconGenesis = GnosisSpecProvider.BeaconChainGenesisTimestampConst;
    private const ulong MainnetSlotLength = 12;
    private const ulong GnosisSlotLength = 5;
    private const ulong MainnetHeadSlot = 13_000_000;
    private const ulong GnosisHeadSlot = 20_000_000;
    private const ulong MainnetHeadTime = MainnetBeaconGenesis + MainnetHeadSlot * MainnetSlotLength;
    private const ulong GnosisHeadTime = GnosisBeaconGenesis + GnosisHeadSlot * GnosisSlotLength;

    /// <summary>
    /// The EIP-7843 slot a simulated header carries must follow the spec resolved for that header, whatever
    /// <see cref="BlockHeader.CreateSimulatedChild"/> hands over. The header RLP carries the field whenever it
    /// is set, so a slot left on a pre-fork header would change the block hash the simulation reports.
    /// </summary>
    [TestCase(true, null, ExpectedResult = 0UL, TestName = "seeds 0 without a beacon genesis or a parent slot")]
    [TestCase(true, 41UL, ExpectedResult = 42UL, TestName = "advances the parent's slot")]
    [TestCase(false, null, ExpectedResult = null, TestName = "leaves the slot unset before the fork")]
    [TestCase(false, 41UL, ExpectedResult = null, TestName = "clears the slot the simulated child arrived with before the fork")]
    public ulong? Simulated_header_slot_follows_the_resolved_spec(bool eip7843Enabled, ulong? parentSlot)
    {
        TestSpecProvider specProvider = new(eip7843Enabled ? Amsterdam.Instance : Prague.Instance);
        BlockHeader parent = Build.A.BlockHeader.WithNumber(10).WithSlotNumber(parentSlot).TestObject;

        return GetCallHeaderSlot(specProvider, MainnetSlotLength, parent, overrideTime: null);
    }

    /// <summary>
    /// Where the spec carries a beacon genesis, the slot is the beacon chain's own for the block's timestamp, taken after
    /// <c>blockOverrides.time</c> and ahead of the parent's slot + 1. A slot length or genesis that does not fit the parent
    /// falls back rather than producing a plausible-looking wrong slot.
    /// </summary>
    [TestCaseSource(nameof(DerivedSlotCases))]
    public ulong? Simulated_header_slot_is_derived_from_the_beacon_genesis(
        ulong beaconGenesis, ulong secondsPerSlot, ulong parentTime, ulong? parentSlot, ulong? overrideTime)
    {
        TestSpecProvider specProvider = new(Amsterdam.Instance) { BeaconChainGenesisTimestamp = beaconGenesis };
        BlockHeader parent = Build.A.BlockHeader.WithNumber(10).WithTimestamp(parentTime).WithSlotNumber(parentSlot).TestObject;

        return GetCallHeaderSlot(specProvider, secondsPerSlot, parent, overrideTime);
    }

    private static IEnumerable<TestCaseData> DerivedSlotCases()
    {
        yield return new TestCaseData(MainnetBeaconGenesis, MainnetSlotLength, MainnetHeadTime, null, MainnetHeadTime + MainnetSlotLength)
        { ExpectedResult = MainnetHeadSlot + 1, TestName = "mainnet head without a slot gets the real next slot" };
        yield return new TestCaseData(GnosisBeaconGenesis, GnosisSlotLength, GnosisHeadTime, null, GnosisHeadTime + GnosisSlotLength)
        { ExpectedResult = GnosisHeadSlot + 1, TestName = "gnosis head gets the real next slot from 5-second slots" };
        yield return new TestCaseData(MainnetBeaconGenesis, MainnetSlotLength, MainnetHeadTime, null, null)
        { ExpectedResult = MainnetHeadSlot + 1, TestName = "without a time override the synthesised timestamp is used" };
        yield return new TestCaseData(MainnetBeaconGenesis, MainnetSlotLength, MainnetHeadTime, null, MainnetHeadTime + 5 * MainnetSlotLength)
        { ExpectedResult = MainnetHeadSlot + 5, TestName = "a time override five slots ahead is honoured" };
        yield return new TestCaseData(MainnetBeaconGenesis, MainnetSlotLength, MainnetHeadTime, MainnetHeadSlot, MainnetHeadTime + 5 * MainnetSlotLength)
        { ExpectedResult = MainnetHeadSlot + 5, TestName = "a derivable slot beats the parent's slot plus one" };
        yield return new TestCaseData(GnosisBeaconGenesis, MainnetSlotLength, GnosisHeadTime, null, GnosisHeadTime + MainnetSlotLength)
        { ExpectedResult = 0UL, TestName = "a slot length that does not fit the parent falls back" };
        yield return new TestCaseData(MainnetBeaconGenesis, MainnetSlotLength, MainnetHeadTime, 7UL, MainnetHeadTime + MainnetSlotLength)
        { ExpectedResult = 8UL, TestName = "a parent slot that disagrees with the grid falls back to the parent's slot plus one" };
        yield return new TestCaseData(GnosisBeaconGenesis, MainnetSlotLength, GnosisHeadTime, GnosisHeadSlot, GnosisHeadTime + MainnetSlotLength)
        { ExpectedResult = GnosisHeadSlot + 1, TestName = "a slot length too long for a parent carrying a slot gives the parent's slot plus one" };
        yield return new TestCaseData(GnosisBeaconGenesis, MainnetSlotLength, GnosisHeadTime + 4 * GnosisSlotLength, null, null)
        { ExpectedResult = 8_333_336UL, TestName = "known residual: a slot length that fits a slotless head on both grids gives a wrong slot" };
        yield return new TestCaseData(MainnetBeaconGenesis, 0UL, MainnetHeadTime, null, MainnetHeadTime + MainnetSlotLength)
        { ExpectedResult = 0UL, TestName = "a zero slot length falls back" };
        yield return new TestCaseData(MainnetBeaconGenesis, MainnetSlotLength, MainnetBeaconGenesis - 1_204, null, MainnetBeaconGenesis + 5 * MainnetSlotLength)
        { ExpectedResult = 0UL, TestName = "a parent before the beacon genesis falls back" };
        yield return new TestCaseData(MainnetBeaconGenesis, MainnetSlotLength, MainnetBeaconGenesis, null, MainnetBeaconGenesis - 1)
        { ExpectedResult = 0UL, TestName = "a timestamp before the beacon genesis falls back" };
    }

    /// <summary>
    /// A time override that falls between slots must neither repeat the head's slot nor switch derivation off for the
    /// blocks after it, whether or not the head carries a slot: each block reports the beacon slot of its time, kept above
    /// its parent's.
    /// </summary>
    [Test]
    public void Off_grid_time_override_keeps_slots_derived_and_increasing([Values(null, MainnetHeadSlot)] ulong? headSlot)
    {
        TestSpecProvider specProvider = new(Amsterdam.Instance) { BeaconChainGenesisTimestamp = MainnetBeaconGenesis };
        BlockHeader head = Build.A.BlockHeader.WithNumber(10).WithTimestamp(MainnetHeadTime).WithSlotNumber(headSlot).TestObject;

        BlockHeader first = GetCallHeader(specProvider, MainnetSlotLength, head, MainnetHeadTime + 1);
        BlockHeader second = GetCallHeader(specProvider, MainnetSlotLength, first, MainnetHeadTime + 1 + 3 * MainnetSlotLength);

        Assert.That(new[] { first.SlotNumber, second.SlotNumber }, Is.EqualTo(new ulong?[] { MainnetHeadSlot + 1, MainnetHeadSlot + 3 }));
    }

    private static ulong? GetCallHeaderSlot(ISpecProvider specProvider, ulong secondsPerSlot, BlockHeader parent, ulong? overrideTime) =>
        GetCallHeader(specProvider, secondsPerSlot, parent, overrideTime).SlotNumber;

    private static BlockHeader GetCallHeader(ISpecProvider specProvider, ulong secondsPerSlot, BlockHeader parent, ulong? overrideTime)
    {
        SimulateBridgeHelper helper = new(new BlocksConfig { SecondsPerSlot = secondsPerSlot }, specProvider);
        BlockStateCall<TransactionWithSourceDetails> block = new()
        {
            BlockOverrides = overrideTime is null ? null : new BlockOverride { Time = overrideTime }
        };

        (BlockHeader callHeader, _) = helper.GetCallHeader(specProvider, block, parent, validate: true);
        callHeader.Hash = callHeader.CalculateHash();
        return callHeader;
    }
}
