// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Simulate;

[TestFixture]
public class SimulateBridgeHelperTests
{
    /// <summary>
    /// The EIP-7843 slot a simulated header carries must follow the spec resolved for that header, whatever
    /// <see cref="BlockHeader.CreateSimulatedChild"/> hands over. The header RLP carries the field whenever it
    /// is set, so a slot left on a pre-fork header would change the block hash the simulation reports.
    /// </summary>
    [TestCase(true, null, false, ExpectedResult = 0UL, TestName = "seeds 0 when neither parent nor child has a slot")]
    [TestCase(true, 41UL, false, ExpectedResult = 42UL, TestName = "advances the parent's slot")]
    [TestCase(true, 41UL, true, ExpectedResult = 42UL, TestName = "keeps the slot the simulated child arrived with")]
    [TestCase(false, null, false, ExpectedResult = null, TestName = "leaves the slot unset before the fork")]
    [TestCase(false, 41UL, true, ExpectedResult = null, TestName = "clears the slot the simulated child arrived with before the fork")]
    public ulong? Simulated_header_slot_follows_the_resolved_spec(bool eip7843Enabled, ulong? parentSlot, bool childArrivesWithSlot)
    {
        ISpecProvider specProvider = new TestSpecProvider(eip7843Enabled ? Amsterdam.Instance : Prague.Instance);
        SimulateBridgeHelper helper = new(new BlocksConfig(), specProvider);

        (BlockHeader callHeader, IReleaseSpec spec) = helper.GetCallHeader(
            specProvider, new BlockStateCall<TransactionWithSourceDetails>(), BuildParent(parentSlot, childArrivesWithSlot), validate: true);

        Assert.That(spec.IsEip7843Enabled, Is.EqualTo(eip7843Enabled), "premise: the resolved spec");
        return callHeader.SlotNumber;
    }

    private static BlockHeader BuildParent(ulong? slotNumber, bool childArrivesWithSlot)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(10).WithSlotNumber(slotNumber).TestObject;
        if (!childArrivesWithSlot) return parent;

        return new SlotFillingHeader(
            parent.ParentHash!, parent.UnclesHash!, parent.Beneficiary!, parent.Difficulty,
            parent.Number, parent.GasLimit, parent.Timestamp, parent.ExtraData!, slotNumber)
        {
            Hash = parent.Hash
        };
    }

    // Stands in for a header whose CreateSimulatedChild already fills the slot in.
    private sealed class SlotFillingHeader(
        Hash256 parentHash, Hash256 unclesHash, Address beneficiary, in UInt256 difficulty,
        ulong number, ulong gasLimit, ulong timestamp, byte[] extraData, ulong? slotNumber)
        : BlockHeader(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData, slotNumber: slotNumber)
    {
        public override BlockHeader CreateSimulatedChild(ulong timestamp)
        {
            BlockHeader child = base.CreateSimulatedChild(timestamp);
            child.SlotNumber = (SlotNumber ?? 0) + 1;
            return child;
        }
    }
}
