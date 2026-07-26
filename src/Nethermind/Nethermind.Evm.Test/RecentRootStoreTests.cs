// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class RecentRootStoreTests
{
    private static readonly Address Source = TestItem.AddressA;
    private static readonly ValueHash256 Salt = TestItem.KeccakA.ValueHash256;
    private static readonly ValueHash256 Root = TestItem.KeccakB.ValueHash256;
    private static readonly ValueHash256 OtherRoot = TestItem.KeccakC.ValueHash256;
    private static readonly IReleaseSpec Spec = Amsterdam.Instance;

    [Test]
    public void SourceId_is_deterministic_and_distinct_per_input()
    {
        ValueHash256 baseline = RecentRootStore.SourceId(Source, Salt);

        Assert.That(RecentRootStore.SourceId(Source, Salt), Is.EqualTo(baseline));
        Assert.That(RecentRootStore.SourceId(TestItem.AddressB, Salt), Is.Not.EqualTo(baseline));
        Assert.That(RecentRootStore.SourceId(Source, OtherRoot), Is.Not.EqualTo(baseline));
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
            RecentRootStore.Write(state, Source, Salt, Root, writeSlot, Spec);
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
            RecentRootStore.Write(state, Source, Salt, Root, writeSlot, Spec);
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
            RecentRootStore.Write(state, Source, Salt, Root, writeSlot, Spec);
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
            RecentRootStore.Write(state, Source, Salt, Root, writtenSlot, Spec);
            ValueHash256 sourceId = RecentRootStore.SourceId(Source, Salt);

            Assert.That(
                RecentRootStore.StorageKey(sourceId, aliasedSlot % Eip8272Constants.RecentRootLength),
                Is.EqualTo(RecentRootStore.StorageKey(sourceId, writtenSlot % Eip8272Constants.RecentRootLength)));

            // The stored entry commits to writtenSlot, so a reference to the aliased slot cannot match.
            Assert.That(RecentRootStore.IsReferenceValid(state, sourceId, aliasedSlot, Root, aliasedSlot + 1), Is.False);
        }
    }

    private static IWorldState CreateState(out IDisposable scope)
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(Eip8272Constants.RecentRootAddress, UInt256.Zero);
        return state;
    }
}
