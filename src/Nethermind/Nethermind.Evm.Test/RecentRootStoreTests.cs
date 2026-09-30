// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

    // Independently computed vectors, so a change to how any of the three preimage buffers is filled cannot
    // silently move a consensus-visible source id, entry commitment or ring-buffer storage key.
    [Test]
    public void Derivations_match_known_vectors()
    {
        Address source = new("0x0f1e2d3c4b5a69788796a5b4c3d2e1f001122334");
        ValueHash256 salt = new("0x00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");
        ValueHash256 root = new("0xaabbccddeeff00112233445566778899aabbccddeeff00112233445566778899");

        ValueHash256 sourceId = RecentRootStore.SourceId(source, salt);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceId, Is.EqualTo(new ValueHash256("0x1d66905c1b538690493aef5321db9b8f2fa27356d2b61fddcc3d6d4545a464fc")));
            Assert.That(RecentRootStore.EntryHash(sourceId, 1234, root), Is.EqualTo(new ValueHash256("0xd3fe50127a6be718dec03d5d2654afe64877d2652e4c79fadd5fab013ac444c5")));
            Assert.That(RecentRootStore.StorageKey(sourceId, 1111), Is.EqualTo(new ValueHash256("0x22a1ba46a5a904217f21d93150fcad98454d19c8d1e13aea70c3cb27e46716a4")));
        }
    }

    /// <remarks>The EIP-8272 reference vector.</remarks>
    [Test]
    public void Derivations_match_the_eip_reference_vector()
    {
        ValueHash256 sourceId = RecentRootStore.SourceId(new Address("0x0000000000000000000000000000000000000001"), default);
        ValueHash256 root = new("0x0000000000000000000000000000000000000000000000000000000000000002");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceId, Is.EqualTo(new ValueHash256("0xb9382d35273c75a50631a3e84d3c75ec9266e2b18c35a627e16cdbf26a18ca85")));
            Assert.That(RecentRootStore.EntryHash(sourceId, 1, root), Is.EqualTo(new ValueHash256("0x0a0d1254c851be5a133b4c9a9e300f5602fc0f43dbe65aa6a66930d4ca0a51b8")));
            Assert.That(RecentRootStore.StorageKey(sourceId, 1), Is.EqualTo(new ValueHash256("0x5f027aa1cbe2df279bf6518edd4b44ea5409fd800189ec35224e10ab05e574c3")));
        }
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

            // Through ReferenceCell, which is what applies the modulo: folding the slots here first would
            // hand StorageKey the same ring index twice and assert nothing about the ring.
            Assert.That(
                RecentRootStore.ReferenceCell(sourceId, aliasedSlot),
                Is.EqualTo(RecentRootStore.ReferenceCell(sourceId, writtenSlot)),
                "a slot a full ring later must land on the cell it aliases");

            // The stored entry commits to writtenSlot, so a reference to the aliased slot cannot match.
            Assert.That(RecentRootStore.IsReferenceValid(state, sourceId, aliasedSlot, Root, aliasedSlot + 1), Is.False);
        }
    }

    private static void Write(IWorldState state, Address source, in ValueHash256 salt, in ValueHash256 root, ulong slot)
    {
        ValueHash256 sourceId = RecentRootStore.SourceId(source, salt);
        StorageCell cell = RecentRootStore.ReferenceCell(sourceId, slot);
        state.Set(cell, RecentRootStore.EntryHash(sourceId, slot, root).ToUInt256());
    }

    private static IWorldState CreateState(out IDisposable scope)
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(Eip8272Constants.RecentRootAddress, UInt256.Zero);
        return state;
    }
}
