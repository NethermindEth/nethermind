// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.BlockProduction;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.BlockProduction;

[TestFixture]
public class BlockHeaderExtensionsTests
{
    [TestCase(7UL, 8UL)]
    [TestCase(null, null)]
    public void GenerateSimulatedPayload_advances_parent_slot_number(ulong? parentSlotNumber, ulong? expected)
    {
        BlockHeader parent = Build.A.BlockHeader.WithSlotNumber(parentSlotNumber).TestObject;

        PayloadAttributes attributes = parent.GenerateSimulatedPayload();

        Assert.That(attributes.SlotNumber, Is.EqualTo(expected));
    }
}
