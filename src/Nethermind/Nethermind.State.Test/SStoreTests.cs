// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[Parallelizable(ParallelScope.All)]
public class SStoreTests
{
    private static readonly Address Address = TestItem.AddressA;
    private static readonly StorageCell Cell = new(Address, UInt256.One);

    /// <summary>Builds the 32-byte big-endian word an SSTORE would pop off the stack.</summary>
    private static EvmWord Word(byte value)
    {
        Span<byte> word = stackalloc byte[32];
        word[31] = value;
        return Unsafe.ReadUnaligned<EvmWord>(ref MemoryMarshal.GetReference(word));
    }

    /// <summary>
    /// Opens a scope where <paramref name="original"/> is the value the cell held at the start of the
    /// transaction, then drives the cell to <paramref name="current"/> with a preceding SStore.
    /// </summary>
    private static (IWorldState state, IDisposable scope) StateWith(byte original, byte current)
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        IDisposable scope = state.BeginScope(IWorldState.PreGenesis);

        state.CreateAccount(Address, 1);
        if (original != 0) state.Set(in Cell, [original]);
        state.Commit(Frontier.Instance);

        // Everything after this snapshot is a new transaction, so `original` is what GetOriginal reports.
        state.TakeSnapshot(newTransactionStart: true);

        if (current != original) state.SStore(in Cell, Word(current));
        return (state, scope);
    }

    // (original, current, new) -> the comparisons EIP-2200 metering reads.
    [TestCase(0, 0, 0, SStoreState.NewSameAsCurrent | SStoreState.CurrentIsZero, TestName = "Noop_on_zero")]
    [TestCase(1, 1, 1, SStoreState.NewSameAsCurrent, TestName = "Noop_on_nonzero")]
    [TestCase(0, 0, 1, SStoreState.CurrentIsZero | SStoreState.CurrentSameAsOriginal | SStoreState.OriginalIsZero, TestName = "Fresh_set")]
    [TestCase(1, 1, 0, SStoreState.CurrentSameAsOriginal, TestName = "Fresh_clear")]
    [TestCase(1, 1, 2, SStoreState.CurrentSameAsOriginal, TestName = "Fresh_overwrite")]
    [TestCase(0, 1, 0, SStoreState.OriginalIsZero | SStoreState.NewSameAsOriginal, TestName = "Dirty_reverted_to_zero_original")]
    [TestCase(1, 2, 1, SStoreState.NewSameAsOriginal, TestName = "Dirty_reverted_to_nonzero_original")]
    [TestCase(1, 2, 3, SStoreState.None, TestName = "Dirty_overwrite")]
    [TestCase(1, 0, 1, SStoreState.CurrentIsZero | SStoreState.NewSameAsOriginal, TestName = "Dirty_cleared_then_restored")]
    public void SStore_reports_comparisons_and_writes(byte original, byte current, byte newValue, SStoreState expected)
    {
        (IWorldState state, IDisposable scope) = StateWith(original, current);
        using (scope)
        {
            Assert.That(state.SStore(in Cell, Word(newValue)), Is.EqualTo(expected));

            byte[] expectedStored = newValue == 0 ? [0] : [newValue];
            Assert.That(state.Get(in Cell).ToArray(), Is.EqualTo(expectedStored));
        }
    }

    /// <summary>
    /// The wrapped state performs the write, so <see cref="TracedAccessWorldState.Set"/> never sees it. The
    /// decorator's own SStore must record the block-access-list change instead — and only when it writes.
    /// </summary>
    [TestCase(7, 7, 0, TestName = "Noop_records_no_storage_change")]
    [TestCase(7, 8, 1, TestName = "Write_records_storage_change")]
    public void SStore_records_block_access_list_change_only_when_it_writes(byte original, byte newValue, int expectedChanges)
    {
        IWorldState inner = TestWorldStateFactory.CreateForTest();
        Hash256 stateRoot;
        using (inner.BeginScope(IWorldState.PreGenesis))
        {
            inner.CreateAccount(Address, 1);
            inner.Set(in Cell, [original]);
            inner.Commit(Amsterdam.Instance, isGenesis: true);
            inner.CommitTree(0);
            stateRoot = inner.StateRoot;
        }

        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(0).TestObject;
        TracedAccessWorldState tracing = new(inner, parallel: false);
        tracing.SetGeneratingBlockAccessList(new());
        using IDisposable scope = tracing.BeginScope(baseBlock);
        tracing.SetIndex(0);

        tracing.SStore(in Cell, Word(newValue));

        AccountChangesAtIndex changes = tracing.GetGeneratingBlockAccessList()!.GetAccountChanges(Address)!;
        Assert.That(changes.StorageChangeCount, Is.EqualTo(expectedChanges));
    }
}
