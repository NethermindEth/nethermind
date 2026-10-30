// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class FoldWorkingStateTests
{
    [Test]
    public void Reuses_writers_folded_ahead_buffers_and_spare_states()
    {
        // A fresh state rather than a rented one, as other tests share the pool.
        using TrieUpdater<PbtVariableTreeKey, PbtStorageNodePath>.FoldWorkingState state = new();

        PbtNodeGroupWriter writer = state.RentWriter(8);
        writer.Append(PbtThreeLevelGroupGeometry.RootPosition, 64);
        state.ReturnWriter(writer);
        PbtNodeGroupWriter reused = state.RentWriter(16);
        PbtNodeGroupWriter nested = state.RentWriter(16);

        state.RentFoldedAhead(out byte[] outerEncodings, out _);
        state.RentFoldedAhead(out byte[] innerEncodings, out TrieUpdater.SlotNode[] innerNodes);
        state.ReturnFoldedAhead(innerEncodings, innerNodes);
        state.RentFoldedAhead(out byte[] reusedEncodings, out _);

        TrieUpdater<PbtVariableTreeKey, PbtStorageNodePath>.FoldWorkingState spare, reusedSpare;
        using (TrieUpdater<PbtVariableTreeKey, PbtStorageNodePath>.SpareWorkingStates spares = new())
        {
            spare = spares.Rent();
            spares.Return(spare);
            reusedSpare = spares.Rent();
            spares.Return(reusedSpare);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reused, Is.SameAs(writer), "a returned writer is rented again");
            Assert.That(reused.WrittenCount, Is.Zero, "a rented writer starts empty");
            Assert.That(nested, Is.Not.SameAs(writer), "a writer in use is not rented again");
            Assert.That(innerEncodings, Is.Not.SameAs(outerEncodings), "nested frames hold buffers of their own");
            Assert.That(reusedEncodings, Is.SameAs(innerEncodings), "returned buffers are rented again");
            Assert.That(reusedSpare, Is.SameAs(spare), "a returned spare is reused within the fold");
        }
        state.ReturnWriter(reused);
        state.ReturnWriter(nested);
    }
}
