// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class RollupXObservationMachineTests
{
    [Test]
    public void Create_RequiresABoundedAuthenticatedAncestorChain([Values("none", "height", "parent", "empty", "oversized")] string change)
    {
        Block parent = Build.A.Block.WithNumber(1).TestObject;
        Block block = Build.A.Block.WithNumber(change == "height" ? 3UL : 2UL)
            .WithParentHash(change == "parent" ? TestItem.KeccakA : parent.Hash!).TestObject;
        int count = change switch { "empty" => 0, "oversized" => 257, _ => 1 };
        ArrayPoolList<byte[]> headers = new(count);
        for (int i = 0; i < count; i++) headers.Add(Rlp.Encode(parent.Header).Bytes);
        using Witness witness = new()
        {
            Headers = headers,
            Codes = new ArrayPoolList<byte[]>(0),
            Keys = new ArrayPoolList<byte[]>(0),
            State = new ArrayPoolList<byte[]>(0),
        };

        if (change == "none")
        {
            using RollupXObservationMachine machine = RollupXObservationMachine.Create(witness, block.Header, new TestSpecProvider(Osaka.Instance), LimboLogs.Instance);
            Assert.That(machine, Is.Not.Null);
        }
        else
            Assert.That(() => RollupXObservationMachine.Create(witness, block.Header, new TestSpecProvider(Osaka.Instance), LimboLogs.Instance),
                Throws.TypeOf<EezSettlementException>());
    }
}
