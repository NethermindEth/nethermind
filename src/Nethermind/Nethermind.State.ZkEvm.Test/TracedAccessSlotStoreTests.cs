// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.ZkEvm.Test;

public class TracedAccessSlotStoreTests
{
    private static readonly Address Contract = new("0x00000000000000000000000000000000000000c0");
    private static readonly StorageCell Cell = new(Contract, 1);
    private static readonly StorageCell OtherCell = new(Contract, 2);
    private static readonly UInt256 TrieValue = 7;

    public enum Scenario { NewValue, SameValue, AfterClear, OtherReadThenSameValue, OtherReadThenNewValue, TwoNewValues, NewValueThenOriginal }

    /// <summary>
    /// SSTOREs through <see cref="IGuestSlotStore"/> must read the same values, leave the same storage and record the same
    /// block access list as through <see cref="IWorldState.Get"/>, <see cref="IWorldState.GetOriginal"/> and
    /// <see cref="IWorldState.Set(in StorageCell, in UInt256, in UInt256)"/>.
    /// </summary>
    [Test]
    public void Slot_store_store_matches_get_and_set([Values] Scenario scenario, [Values] bool parallel, [Values] bool generating)
    {
        UInt256[] newValues = scenario switch
        {
            Scenario.SameValue or Scenario.OtherReadThenSameValue => [TrieValue],
            Scenario.TwoNewValues => [5, 9],
            Scenario.NewValueThenOriginal => [5, TrieValue],
            _ => [5],
        };

        (TracedAccessWorldState expected, GuestWorldState expectedGuest, IDisposable expectedScope) = CreateState(parallel, generating);
        (TracedAccessWorldState actual, GuestWorldState actualGuest, IDisposable actualScope) = CreateState(parallel, generating);
        using (expectedScope)
        using (actualScope)
        {
            Prepare(expected, scenario);
            Prepare(actual, scenario);
            List<(UInt256 Current, UInt256 Original)> expectedReads = [];
            List<(UInt256 Current, UInt256 Original)> actualReads = [];
            List<int> slots = [];
            IGuestSlotStore slotStore = actual;
            foreach (UInt256 newValue in newValues)
            {
                expected.Get(in Cell, out UInt256 expectedCurrent);
                expected.GetOriginal(in Cell, out UInt256 expectedOriginal);
                expectedReads.Add((expectedCurrent, expectedOriginal));
                // As the SSTORE handler, write only a changed value.
                if (expectedCurrent != newValue) expected.Set(in Cell, in newValue, in expectedCurrent);

                int slot = slotStore.ReadForStore(in Cell, out UInt256 actualCurrent, out UInt256 actualOriginal);
                actualReads.Add((actualCurrent, actualOriginal));
                slots.Add(slot);
                if (actualCurrent != newValue) slotStore.WriteAt(slot, in Cell, in actualCurrent, in newValue);
            }

            expectedGuest.Get(in Cell, out UInt256 expectedStored);
            actualGuest.Get(in Cell, out UInt256 actualStored);
            AccountChangesAtIndex? expectedChanges = expected.GetGeneratingBlockAccessList()?.GetAccountChanges(Contract);
            AccountChangesAtIndex? actualChanges = actual.GetGeneratingBlockAccessList()?.GetAccountChanges(Contract);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actualReads, Is.EqualTo(expectedReads), "current and original");
                Assert.That(slots.All(slot => slot >= 0), Is.EqualTo(!parallel), "slot store used");
                Assert.That(actualStored, Is.EqualTo(expectedStored), "stored");
                Assert.That(actualChanges is null, Is.EqualTo(expectedChanges is null), "account listed");
                Assert.That(actualChanges is null, Is.EqualTo(!generating), "account listed only while generating");
                if (expectedChanges is not null && actualChanges is not null)
                {
                    Assert.That(actualChanges.StorageChanges.ToArray(), Is.EqualTo(expectedChanges.StorageChanges.ToArray()), "storage changes");
                    Assert.That(actualChanges.StorageReads, Is.EquivalentTo(expectedChanges.StorageReads), "storage reads");
                    Assert.That(actualChanges.TryGetPreTxStorage(in Cell.Index, out UInt256 actualPreTx), Is.EqualTo(expectedChanges.TryGetPreTxStorage(in Cell.Index, out UInt256 expectedPreTx)), "prestate seeded");
                    Assert.That(actualPreTx, Is.EqualTo(expectedPreTx), "prestate");
                }
            }
        }
    }

    private static void Prepare(TracedAccessWorldState state, Scenario scenario)
    {
        if (scenario is not (Scenario.AfterClear or Scenario.OtherReadThenSameValue or Scenario.OtherReadThenNewValue)) return;

        // A read of another slot moves the last read cell away from the stored one; before a clear it also lets the clear
        // find storage to clear, and leaves the stored cell unloaded before it.
        state.Get(in OtherCell, out _);
        if (scenario == Scenario.AfterClear) state.ClearStorage(Contract);
    }

    private static (TracedAccessWorldState State, GuestWorldState Guest, IDisposable Scope) CreateState(bool parallel, bool generating)
    {
        IWorldStateScopeProvider.IStorageTree storage = Substitute.For<IWorldStateScopeProvider.IStorageTree>();
        storage.When(tree => tree.Get(in Arg.Any<UInt256>(), out Arg.Any<UInt256>())).Do(call => call[1] = TrieValue);
        GuestWorldState guest = new(GuestStorageOriginalsTests.HashFreeScopeProvider(storage));
        TracedAccessWorldState state = new(guest, parallel);
        if (generating) state.SetGeneratingBlockAccessList(new());
        IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        if (generating) state.SetIndex(0);
        return (state, guest, scope);
    }
}
