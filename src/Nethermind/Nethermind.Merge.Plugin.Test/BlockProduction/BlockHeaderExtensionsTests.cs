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
    [Test]
    public void GenerateSimulatedPayload_preserves_parent_slot_number()
    {
        BlockHeader parent = Build.A.BlockHeader.WithSlotNumber(7).TestObject;

        PayloadAttributes attributes = parent.GenerateSimulatedPayload();

        Assert.That(attributes.SlotNumber, Is.EqualTo(7));
    }

    [Test]
    public void GenerateSimulatedPayload_without_parent_slot_stays_without_slot()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        parent.SlotNumber = null;

        PayloadAttributes attributes = parent.GenerateSimulatedPayload();

        Assert.That(attributes.SlotNumber, Is.Null);
    }
}
