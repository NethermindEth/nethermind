// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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

    /// <summary>SLoad must widen the stored minimal-length value back to the word the EVM stack holds.</summary>
    [TestCase("", TestName = "SLoad_of_unset_slot_is_zero")]
    [TestCase("01", TestName = "SLoad_pads_single_byte")]
    [TestCase("0102", TestName = "SLoad_pads_two_bytes")]
    [TestCase("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20", TestName = "SLoad_full_width")]
    public void SLoad_returns_zero_padded_word(string storedHex)
    {
        byte[] stored = Bytes.FromHexString(storedHex);

        IWorldState state = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(Address, 1);
        if (stored.Length != 0) state.Set(in Cell, stored);
        state.Commit(Frontier.Instance);

        byte[] expected = new byte[32];
        stored.CopyTo(expected.AsSpan(32 - stored.Length));

        EvmWord loaded = state.SLoad(in Cell);
        Assert.That(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref loaded, 1)).ToArray(), Is.EqualTo(expected));
    }

    /// <summary>
    /// SLoad reads the backing store directly rather than through Get, but must still capture the original
    /// value: a later SStore reads it (EIP-2200), and would otherwise throw or misreport the reversal.
    /// </summary>
    [Test]
    public void SLoad_captures_original_for_a_later_SStore()
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(Address, 1);
        state.Set(in Cell, [5]);
        state.Commit(Frontier.Instance);
        state.TakeSnapshot(newTransactionStart: true);

        // The only read of the slot this transaction, so SStore's GetOriginal depends on it.
        Assert.That(state.SLoad(in Cell), Is.EqualTo(Word(5)));

        // 5 -> 9 is the first write: current still equals the captured original.
        Assert.That(state.SStore(in Cell, Word(9)), Is.EqualTo(SStoreState.CurrentSameAsOriginal));
        // 9 -> 5 reverts to the original SLoad observed.
        Assert.That(state.SStore(in Cell, Word(5)), Is.EqualTo(SStoreState.NewSameAsOriginal));
    }

    /// <summary>SStore then SLoad must observe the value just written, through the same word representation.</summary>
    [TestCase(0, TestName = "SLoad_after_SStore_zero")]
    [TestCase(1, TestName = "SLoad_after_SStore_one")]
    [TestCase(255, TestName = "SLoad_after_SStore_max_byte")]
    public void SLoad_observes_preceding_SStore(byte newValue)
    {
        (IWorldState state, IDisposable scope) = StateWith(original: 3, current: 3);
        using (scope)
        {
            state.SStore(in Cell, Word(newValue));
            Assert.That(state.SLoad(in Cell), Is.EqualTo(Word(newValue)));
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
