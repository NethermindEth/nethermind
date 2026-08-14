// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class RecentRootStoreTests
{
    private static readonly ValueHash256 SourceId = TestItem.KeccakD.ValueHash256;
    private static readonly ValueHash256 OtherSourceId = TestItem.KeccakE.ValueHash256;
    private static readonly ValueHash256 Root = TestItem.KeccakB.ValueHash256;
    private static readonly ValueHash256 OtherRoot = TestItem.KeccakC.ValueHash256;
    private static readonly Address Source = TestItem.AddressA;
    private static readonly ValueHash256 Salt = TestItem.KeccakA.ValueHash256;

    // Every concatenation in EIP-8272 is fixed-width, and an address is 20 bytes there, so the
    // preimage is 52 bytes. Left-padding the address to a word changes every source id, which is
    // consensus-visible on the first reference-carrying transaction.
    [Test]
    public void SourceId_hashes_the_address_unpadded()
    {
        Span<byte> preimage = stackalloc byte[Address.Size + ValueHash256.MemorySize];
        Source.Bytes.CopyTo(preimage);
        Salt.Bytes.CopyTo(preimage[Address.Size..]);

        Assert.That(RecentRootStore.SourceId(Source, Salt), Is.EqualTo(ValueKeccak.Compute(preimage)));
    }

    [Test]
    public void EntryHash_is_deterministic_and_distinct_per_input()
    {
        ValueHash256 baseline = RecentRootStore.EntryHash(SourceId, 100, Root);

        Assert.That(RecentRootStore.EntryHash(SourceId, 100, Root), Is.EqualTo(baseline));
        Assert.That(RecentRootStore.EntryHash(SourceId, 101, Root), Is.Not.EqualTo(baseline));
        Assert.That(RecentRootStore.EntryHash(SourceId, 100, OtherRoot), Is.Not.EqualTo(baseline));
        Assert.That(RecentRootStore.EntryHash(OtherSourceId, 100, Root), Is.Not.EqualTo(baseline));
    }

    [Test]
    public void StorageKey_is_deterministic_and_distinct_per_input()
    {
        ValueHash256 baseline = RecentRootStore.StorageKey(SourceId, 5);

        Assert.That(RecentRootStore.StorageKey(SourceId, 5), Is.EqualTo(baseline));
        Assert.That(RecentRootStore.StorageKey(SourceId, 6), Is.Not.EqualTo(baseline));
        Assert.That(RecentRootStore.StorageKey(OtherSourceId, 5), Is.Not.EqualTo(baseline));
    }

    [Test]
    public void EntryHash_and_StorageKey_use_distinct_domains() =>
        Assert.That(
            RecentRootStore.EntryHash(SourceId, 5, Root),
            Is.Not.EqualTo(RecentRootStore.StorageKey(SourceId, 5)));

    [Test]
    public void Reference_validity_window_boundaries()
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            const ulong writeSlot = 100_000;
            Write(state, SourceId, Root, writeSlot);

            Assert.That(RecentRootStore.IsReferenceValid(state, SourceId, writeSlot, Root, writeSlot + 1), Is.True);
            Assert.That(RecentRootStore.IsReferenceValid(state, SourceId, writeSlot, Root, writeSlot), Is.False);
            Assert.That(
                RecentRootStore.IsReferenceValid(state, SourceId, writeSlot, Root, writeSlot + Eip8272Constants.RecentRootUsableWindow),
                Is.True);
            Assert.That(
                RecentRootStore.IsReferenceValid(state, SourceId, writeSlot, Root, writeSlot + Eip8272Constants.RecentRootUsableWindow + 1),
                Is.False);
        }
    }

    [Test]
    public void Write_then_validate_round_trips()
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            const ulong writeSlot = 1000;
            const ulong currentSlot = 1001;
            Write(state, SourceId, Root, writeSlot);

            Assert.That(RecentRootStore.IsReferenceValid(state, SourceId, writeSlot, Root, currentSlot), Is.True);
        }
    }

    [Test]
    public void Reference_with_mismatched_root_slot_or_source_does_not_validate()
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            const ulong writeSlot = 1000;
            const ulong currentSlot = 1001;
            Write(state, SourceId, Root, writeSlot);

            Assert.That(RecentRootStore.IsReferenceValid(state, SourceId, writeSlot, OtherRoot, currentSlot), Is.False);
            Assert.That(RecentRootStore.IsReferenceValid(state, SourceId, writeSlot - 1, Root, currentSlot), Is.False);
            Assert.That(RecentRootStore.IsReferenceValid(state, OtherSourceId, writeSlot, Root, currentSlot), Is.False);
        }
    }

    [Test]
    public void Aliased_slot_does_not_validate_against_stale_reference()
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            const ulong writtenSlot = 5;
            ulong aliasedSlot = writtenSlot + Eip8272Constants.RecentRootLength;
            Write(state, SourceId, Root, writtenSlot);

            Assert.That(
                RecentRootStore.StorageKey(SourceId, aliasedSlot % Eip8272Constants.RecentRootLength),
                Is.EqualTo(RecentRootStore.StorageKey(SourceId, writtenSlot % Eip8272Constants.RecentRootLength)));

            Assert.That(RecentRootStore.IsReferenceValid(state, SourceId, aliasedSlot, Root, aliasedSlot + 1), Is.False);
        }
    }

    [Test]
    public void AreReferencesValid_true_for_empty_set()
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            (ValueHash256, ulong, ValueHash256)[] references = [];
            Assert.That(RecentRootStore.AreReferencesValid(state, references, currentSlot: 200), Is.True);
        }
    }

    [Test]
    public void AreReferencesValid_true_when_every_reference_is_valid()
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            Write(state, SourceId, Root, 100);
            Write(state, SourceId, OtherRoot, 150);

            (ValueHash256, ulong, ValueHash256)[] references =
            [
                (SourceId, 100UL, Root),
                (SourceId, 150UL, OtherRoot)
            ];
            Assert.That(RecentRootStore.AreReferencesValid(state, references, currentSlot: 200), Is.True);
        }
    }

    [Test]
    public void AreReferencesValid_false_when_a_reference_is_invalid()
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            Write(state, SourceId, Root, 100);

            (ValueHash256, ulong, ValueHash256)[] references =
            [
                (SourceId, 100UL, Root),
                (SourceId, 100UL, OtherRoot)
            ];
            Assert.That(RecentRootStore.AreReferencesValid(state, references, currentSlot: 200), Is.False);
        }
    }

    [Test]
    public void AreReferencesValid_true_for_duplicate_valid_references()
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            Write(state, SourceId, Root, 100);

            (ValueHash256, ulong, ValueHash256)[] references =
            [
                (SourceId, 100UL, Root),
                (SourceId, 100UL, Root)
            ];
            Assert.That(RecentRootStore.AreReferencesValid(state, references, currentSlot: 200), Is.True);
        }
    }

    [TestCase(0, ExpectedResult = true)]
    [TestCase(1, ExpectedResult = false)]
    public bool AreReferencesValid_enforces_the_reference_cap(int overCap)
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            (ValueHash256, ulong, ValueHash256)[] references = new (ValueHash256, ulong, ValueHash256)[Eip8272Constants.MaxRecentRootReferences + overCap];
            for (int i = 0; i < references.Length; i++)
            {
                ulong slot = (ulong)(100 + i);
                Write(state, SourceId, Root, slot);
                references[i] = (SourceId, slot, Root);
            }
            return RecentRootStore.AreReferencesValid(state, references, currentSlot: 500);
        }
    }

    private static void Write(IWorldState state, in ValueHash256 sourceId, in ValueHash256 root, ulong slot)
    {
        StorageCell cell = new(
            Eip8272Constants.RecentRootAddress,
            RecentRootStore.StorageKey(sourceId, slot % Eip8272Constants.RecentRootLength).ToUInt256());
        state.Set(cell, RecentRootStore.EntryHash(sourceId, slot, root).Bytes.WithoutLeadingZeros().ToArray());
    }

    private static IWorldState CreateState(out IDisposable scope)
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(Eip8272Constants.RecentRootAddress, UInt256.Zero);
        return state;
    }
}
