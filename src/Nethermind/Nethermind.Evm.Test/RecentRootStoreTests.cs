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
    private static readonly Address Source = TestItem.AddressA;
    private static readonly ValueHash256 Salt = TestItem.KeccakA.ValueHash256;
    private static readonly ValueHash256 Root = TestItem.KeccakB.ValueHash256;
    private static readonly ValueHash256 OtherRoot = TestItem.KeccakC.ValueHash256;

    [Test]
    public void SourceId_is_deterministic_and_distinct_per_input()
    {
        ValueHash256 baseline = RecentRootStore.SourceId(Source, Salt);

        Assert.That(RecentRootStore.SourceId(Source, Salt), Is.EqualTo(baseline));
        Assert.That(RecentRootStore.SourceId(TestItem.AddressB, Salt), Is.Not.EqualTo(baseline));
        Assert.That(RecentRootStore.SourceId(Source, OtherRoot), Is.Not.EqualTo(baseline));
    }

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
        ValueHash256 sourceId = RecentRootStore.SourceId(Source, Salt);
        ValueHash256 otherSource = RecentRootStore.SourceId(TestItem.AddressB, Salt);
        ValueHash256 baseline = RecentRootStore.EntryHash(sourceId, 100, Root);

        Assert.That(RecentRootStore.EntryHash(sourceId, 100, Root), Is.EqualTo(baseline));
        Assert.That(RecentRootStore.EntryHash(sourceId, 101, Root), Is.Not.EqualTo(baseline));
        Assert.That(RecentRootStore.EntryHash(sourceId, 100, OtherRoot), Is.Not.EqualTo(baseline));
        Assert.That(RecentRootStore.EntryHash(otherSource, 100, Root), Is.Not.EqualTo(baseline));
    }

    [Test]
    public void StorageKey_is_deterministic_and_distinct_per_input()
    {
        ValueHash256 sourceId = RecentRootStore.SourceId(Source, Salt);
        ValueHash256 otherSource = RecentRootStore.SourceId(TestItem.AddressB, Salt);
        ValueHash256 baseline = RecentRootStore.StorageKey(sourceId, 5);

        Assert.That(RecentRootStore.StorageKey(sourceId, 5), Is.EqualTo(baseline));
        Assert.That(RecentRootStore.StorageKey(sourceId, 6), Is.Not.EqualTo(baseline));
        Assert.That(RecentRootStore.StorageKey(otherSource, 5), Is.Not.EqualTo(baseline));
    }

    [Test]
    public void EntryHash_and_StorageKey_use_distinct_domains()
    {
        ValueHash256 sourceId = RecentRootStore.SourceId(Source, Salt);

        Assert.That(
            RecentRootStore.EntryHash(sourceId, 5, Root),
            Is.Not.EqualTo(RecentRootStore.StorageKey(sourceId, 5)));
    }

    [Test]
    public void Reference_validity_window_boundaries()
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            const ulong writeSlot = 100_000;
            Write(state, Source, Salt, Root, writeSlot);
            ValueHash256 sourceId = RecentRootStore.SourceId(Source, Salt);

            Assert.That(RecentRootStore.IsReferenceValid(state, sourceId, writeSlot, Root, writeSlot + 1), Is.True);
            Assert.That(RecentRootStore.IsReferenceValid(state, sourceId, writeSlot, Root, writeSlot), Is.False);
            Assert.That(
                RecentRootStore.IsReferenceValid(state, sourceId, writeSlot, Root, writeSlot + Eip8272Constants.RecentRootUsableWindow),
                Is.True);
            Assert.That(
                RecentRootStore.IsReferenceValid(state, sourceId, writeSlot, Root, writeSlot + Eip8272Constants.RecentRootUsableWindow + 1),
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
            Write(state, Source, Salt, Root, writeSlot);
            ValueHash256 sourceId = RecentRootStore.SourceId(Source, Salt);

            Assert.That(RecentRootStore.IsReferenceValid(state, sourceId, writeSlot, Root, currentSlot), Is.True);
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
            Write(state, Source, Salt, Root, writeSlot);
            ValueHash256 sourceId = RecentRootStore.SourceId(Source, Salt);
            ValueHash256 wrongSource = RecentRootStore.SourceId(TestItem.AddressB, Salt);

            Assert.That(RecentRootStore.IsReferenceValid(state, sourceId, writeSlot, OtherRoot, currentSlot), Is.False);
            Assert.That(RecentRootStore.IsReferenceValid(state, sourceId, writeSlot - 1, Root, currentSlot), Is.False);
            Assert.That(RecentRootStore.IsReferenceValid(state, wrongSource, writeSlot, Root, currentSlot), Is.False);
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
            Write(state, Source, Salt, Root, writtenSlot);
            ValueHash256 sourceId = RecentRootStore.SourceId(Source, Salt);

            Assert.That(
                RecentRootStore.StorageKey(sourceId, aliasedSlot % Eip8272Constants.RecentRootLength),
                Is.EqualTo(RecentRootStore.StorageKey(sourceId, writtenSlot % Eip8272Constants.RecentRootLength)));

            // The stored entry commits to writtenSlot, so a reference to the aliased slot cannot match.
            Assert.That(RecentRootStore.IsReferenceValid(state, sourceId, aliasedSlot, Root, aliasedSlot + 1), Is.False);
        }
    }

    // The consensus check the processor runs: a set is admissible only when every reference matches
    // the commitment the predeploy holds, and only up to the reference cap.
    [Test]
    public void Validate_true_for_an_absent_and_an_empty_set()
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            using StackAccessTracker accessTracker = new(false);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(RecentRootReferences.Validate(state, null, currentSlot: 200, in accessTracker), Is.True);
                Assert.That(RecentRootReferences.Validate(state, [], currentSlot: 200, in accessTracker), Is.True);
            }
        }
    }

    [Test]
    public void Validate_true_when_every_reference_is_committed()
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            Write(state, Source, Salt, Root, 100);
            Write(state, Source, Salt, OtherRoot, 150);
            ValueHash256 sourceId = RecentRootStore.SourceId(Source, Salt);

            Assert.That(Validate(state, [new(sourceId, 100, Root), new(sourceId, 150, OtherRoot)]), Is.True);
        }
    }

    [Test]
    public void Validate_false_when_a_reference_names_a_root_the_slot_does_not_hold()
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            Write(state, Source, Salt, Root, 100);
            ValueHash256 sourceId = RecentRootStore.SourceId(Source, Salt);

            Assert.That(Validate(state, [new(sourceId, 100, Root), new(sourceId, 100, OtherRoot)]), Is.False);
        }
    }

    [Test]
    public void Validate_true_for_a_repeated_committed_reference()
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            Write(state, Source, Salt, Root, 100);
            ValueHash256 sourceId = RecentRootStore.SourceId(Source, Salt);

            Assert.That(Validate(state, [new(sourceId, 100, Root), new(sourceId, 100, Root)]), Is.True);
        }
    }

    // A header without a slot number cannot place a reference in the window, so it admits none.
    [Test]
    public void Validate_false_without_a_slot_number()
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            Write(state, Source, Salt, Root, 100);
            ValueHash256 sourceId = RecentRootStore.SourceId(Source, Salt);
            using StackAccessTracker accessTracker = new(false);

            Assert.That(RecentRootReferences.Validate(state, [new(sourceId, 100, Root)], currentSlot: null, in accessTracker), Is.False);
        }
    }

    [TestCase(0, ExpectedResult = true)]
    [TestCase(1, ExpectedResult = false)]
    public bool Validate_enforces_the_reference_cap(int overCap)
    {
        IWorldState state = CreateState(out IDisposable scope);
        using (scope)
        {
            ValueHash256 sourceId = RecentRootStore.SourceId(Source, Salt);
            // every reference is individually valid, so only the count decides the result
            RecentRootReference[] references = new RecentRootReference[Eip8272Constants.MaxRecentRootReferences + overCap];
            for (int i = 0; i < references.Length; i++)
            {
                ulong slot = (ulong)(100 + i);
                Write(state, Source, Salt, Root, slot);
                references[i] = new RecentRootReference(sourceId, slot, Root);
            }

            return Validate(state, references, currentSlot: 500);
        }
    }

    private static bool Validate(IWorldState state, RecentRootReference[] references, ulong currentSlot = 200)
    {
        using StackAccessTracker accessTracker = new(false);
        return RecentRootReferences.Validate(state, references, currentSlot, in accessTracker);
    }

    private static void Write(IWorldState state, Address source, in ValueHash256 salt, in ValueHash256 root, ulong slot)
    {
        ValueHash256 sourceId = RecentRootStore.SourceId(source, salt);
        StorageCell cell = RecentRootStore.ReferenceCell(sourceId, slot);
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
