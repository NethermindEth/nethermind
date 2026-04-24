// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Diff;
using Nethermind.StateComposition.Service;
using Nethermind.StateComposition.Test.Helpers;
using Nethermind.StateComposition.Visitors;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Service;

/// <summary>
/// Direct unit tests for the incremental tracker logic on <see cref="StateCompositionStateHolder"/>.
/// These exercise <see cref="StateCompositionStateHolder.ApplyIncrementalDiffAndUpdate"/>
/// with synthetic <see cref="TrieDiff"/> payloads so the refcount bookkeeping for
/// <see cref="CumulativeTrieStats.CodeBytesTotal"/> and the slot-histogram bucketing
/// for <see cref="CumulativeTrieStats.SlotCountHistogram"/> can be asserted without
/// building real tries.
/// </summary>
[TestFixture]
public class StateCompositionStateHolderTests
{
    private static readonly Hash256 AnyRoot = Keccak.Compute("root");

    private static TrieDiff DiffWithPayloads(
        IReadOnlyList<SlotCountChange>? slotCountChanges = null,
        IReadOnlyList<CodeHashChange>? codeHashChanges = null) =>
        new(
            AccountsAdded: 0, AccountsRemoved: 0,
            ContractsAdded: 0, ContractsRemoved: 0,
            AccountTrieBranchesAdded: 0, AccountTrieBranchesRemoved: 0,
            AccountTrieExtensionsAdded: 0, AccountTrieExtensionsRemoved: 0,
            AccountTrieLeavesAdded: 0, AccountTrieLeavesRemoved: 0,
            AccountTrieBytesAdded: 0, AccountTrieBytesRemoved: 0,
            StorageTrieBranchesAdded: 0, StorageTrieBranchesRemoved: 0,
            StorageTrieExtensionsAdded: 0, StorageTrieExtensionsRemoved: 0,
            StorageTrieLeavesAdded: 0, StorageTrieLeavesRemoved: 0,
            StorageTrieBytesAdded: 0, StorageTrieBytesRemoved: 0,
            StorageSlotsAdded: 0, StorageSlotsRemoved: 0,
            ContractsWithStorageAdded: 0, ContractsWithStorageRemoved: 0,
            EmptyAccountsAdded: 0, EmptyAccountsRemoved: 0,
            DepthDelta: new CumulativeDepthStats(),
            SlotCountChanges: slotCountChanges ?? Array.Empty<SlotCountChange>(),
            CodeHashChanges: codeHashChanges ?? Array.Empty<CodeHashChange>());

    private static StateCompositionStateHolder HolderWithBaseline(
        CumulativeTrieStats baseline,
        Dictionary<ValueHash256, long>? slotCountByAddress = null,
        Dictionary<ValueHash256, int>? codeHashRefcounts = null,
        Dictionary<ValueHash256, int>? codeHashSizes = null)
    {
        StateCompositionStateHolder holder = new();
        holder.InitializeIncremental(
            baseline, blockNumber: 1, stateRoot: AnyRoot, depthDistribution: null,
            slotCountByAddress: slotCountByAddress,
            codeHashRefcounts: codeHashRefcounts,
            codeHashSizes: codeHashSizes);
        return holder;
    }

    [Test]
    public void ApplyIncrementalDiff_AddNewCodeHash_AddsToCodeBytesTotal()
    {
        ValueHash256 proxy = Keccak.Compute("proxy").ValueHash256;
        StateCompositionStateHolder holder = HolderWithBaseline(TestDataBuilders.EmptyBaseline());

        TrieDiff diff = DiffWithPayloads(codeHashChanges:
        [
            new CodeHashChange(Keccak.Compute("acc").ValueHash256, CodeHashChange.NoCode, proxy),
        ]);

        CumulativeTrieStats updated = holder.ApplyIncrementalDiffAndUpdate(
            diff, blockNumber: 2, stateRoot: AnyRoot,
            codeSizeLookup: hash => hash == proxy ? 512 : 0);

        Assert.That(updated.CodeBytesTotal, Is.EqualTo(512));
    }

    [Test]
    public void ApplyIncrementalDiff_TwoAccountsShareCodeHash_CountedOnce()
    {
        // Two fresh accounts adopt the same proxy bytecode in one diff.
        // CodeBytesTotal must reflect exactly one GetCode call worth of bytes.
        ValueHash256 proxy = Keccak.Compute("proxy").ValueHash256;
        ValueHash256 acc1 = Keccak.Compute("a1").ValueHash256;
        ValueHash256 acc2 = Keccak.Compute("a2").ValueHash256;
        StateCompositionStateHolder holder = HolderWithBaseline(TestDataBuilders.EmptyBaseline());

        int lookups = 0;
        int Lookup(ValueHash256 h)
        {
            lookups++;
            return h == proxy ? 1024 : 0;
        }

        TrieDiff diff = DiffWithPayloads(codeHashChanges:
        [
            new CodeHashChange(acc1, CodeHashChange.NoCode, proxy),
            new CodeHashChange(acc2, CodeHashChange.NoCode, proxy),
        ]);

        CumulativeTrieStats updated = holder.ApplyIncrementalDiffAndUpdate(
            diff, blockNumber: 2, stateRoot: AnyRoot, codeSizeLookup: Lookup);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.CodeBytesTotal, Is.EqualTo(1024),
                "shared bytecode must be counted exactly once");
            Assert.That(lookups, Is.EqualTo(1),
                "codeSizeLookup must fire only on the first observation of a code hash");
        }
    }

    [Test]
    public void ApplyIncrementalDiff_DropSharedCodeHash_KeepsCodeBytesUntilLastReference()
    {
        // Seed: two accounts share a 1024-byte proxy. First account drops it — bytes
        // stay. Second account drops it — bytes go to zero.
        ValueHash256 proxy = Keccak.Compute("proxy").ValueHash256;
        ValueHash256 acc1 = Keccak.Compute("a1").ValueHash256;
        ValueHash256 acc2 = Keccak.Compute("a2").ValueHash256;

        StateCompositionStateHolder holder = HolderWithBaseline(
            TestDataBuilders.EmptyBaseline(codeBytes: 1024),
            codeHashRefcounts: new Dictionary<ValueHash256, int> { [proxy] = 2 },
            codeHashSizes: new Dictionary<ValueHash256, int> { [proxy] = 1024 });

        TrieDiff dropFirst = DiffWithPayloads(codeHashChanges:
        [
            new CodeHashChange(acc1, proxy, CodeHashChange.NoCode),
        ]);
        CumulativeTrieStats afterFirstDrop = holder.ApplyIncrementalDiffAndUpdate(
            dropFirst, blockNumber: 2, stateRoot: AnyRoot, codeSizeLookup: _ => 0);

        TrieDiff dropSecond = DiffWithPayloads(codeHashChanges:
        [
            new CodeHashChange(acc2, proxy, CodeHashChange.NoCode),
        ]);
        CumulativeTrieStats afterSecondDrop = holder.ApplyIncrementalDiffAndUpdate(
            dropSecond, blockNumber: 3, stateRoot: AnyRoot, codeSizeLookup: _ => 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterFirstDrop.CodeBytesTotal, Is.EqualTo(1024),
                "still one referencer, bytes must stay");
            Assert.That(afterSecondDrop.CodeBytesTotal, Is.Zero,
                "last referencer gone, bytes must release");
        }
    }

    [Test]
    public void ApplyIncrementalDiff_CodeHashSwap_NetChangeIsDelta()
    {
        // One account swaps its implementation from a 1024-byte contract to a 2048-byte one.
        // Old code had only this one referencer, so it should release; new code is new.
        ValueHash256 oldImpl = Keccak.Compute("old").ValueHash256;
        ValueHash256 newImpl = Keccak.Compute("new").ValueHash256;
        ValueHash256 acc = Keccak.Compute("acc").ValueHash256;

        StateCompositionStateHolder holder = HolderWithBaseline(
            TestDataBuilders.EmptyBaseline(codeBytes: 1024),
            codeHashRefcounts: new Dictionary<ValueHash256, int> { [oldImpl] = 1 },
            codeHashSizes: new Dictionary<ValueHash256, int> { [oldImpl] = 1024 });

        TrieDiff diff = DiffWithPayloads(codeHashChanges:
        [
            new CodeHashChange(acc, oldImpl, newImpl),
        ]);
        CumulativeTrieStats updated = holder.ApplyIncrementalDiffAndUpdate(
            diff, blockNumber: 2, stateRoot: AnyRoot,
            codeSizeLookup: h => h == newImpl ? 2048 : 0);

        Assert.That(updated.CodeBytesTotal, Is.EqualTo(2048));
    }

    [Test]
    public void ApplyIncrementalDiff_NewContract_AddsHistogramBucket()
    {
        // Fresh contract lands with 5 slots — bucket = floor(log2(6)) = 2.
        ValueHash256 acc = Keccak.Compute("acc").ValueHash256;
        StateCompositionStateHolder holder = HolderWithBaseline(TestDataBuilders.EmptyBaseline());

        TrieDiff diff = DiffWithPayloads(slotCountChanges:
        [
            new SlotCountChange(acc, 5),
        ]);
        CumulativeTrieStats updated = holder.ApplyIncrementalDiffAndUpdate(
            diff, blockNumber: 2, stateRoot: AnyRoot, codeSizeLookup: _ => 0);

        int expectedBucket = VisitorCounters.ComputeSlotBucket(5);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.SlotCountHistogram[expectedBucket], Is.EqualTo(1));
            long totalContracts = 0;
            foreach (long b in updated.SlotCountHistogram) totalContracts += b;
            Assert.That(totalContracts, Is.EqualTo(1));
        }
    }

    [Test]
    public void ApplyIncrementalDiff_ContractGrowsBucket_MovesBetweenBuckets()
    {
        // Contract starts with 3 slots (bucket floor(log2(4))=2), gains 5 slots (total 8,
        // bucket floor(log2(9))=3). Histogram must decrement bucket 2 and increment bucket 3.
        ValueHash256 acc = Keccak.Compute("acc").ValueHash256;
        int startBucket = VisitorCounters.ComputeSlotBucket(3);
        int endBucket = VisitorCounters.ComputeSlotBucket(8);
        Assert.That(startBucket, Is.Not.EqualTo(endBucket), "bucket picks must actually differ");

        long[] seedHist = new long[CumulativeTrieStats.SlotHistogramLength];
        seedHist[startBucket] = 1;
        StateCompositionStateHolder holder = HolderWithBaseline(
            TestDataBuilders.EmptyBaseline(histogram: seedHist),
            slotCountByAddress: new Dictionary<ValueHash256, long> { [acc] = 3 });

        TrieDiff diff = DiffWithPayloads(slotCountChanges:
        [
            new SlotCountChange(acc, 5),
        ]);
        CumulativeTrieStats updated = holder.ApplyIncrementalDiffAndUpdate(
            diff, blockNumber: 2, stateRoot: AnyRoot, codeSizeLookup: _ => 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.SlotCountHistogram[startBucket], Is.Zero);
            Assert.That(updated.SlotCountHistogram[endBucket], Is.EqualTo(1));
        }
    }

    [Test]
    public void ApplyIncrementalDiff_ContractEmptied_LeavesHistogram()
    {
        // Contract at 10 slots (bucket 3) has all slots wiped. Histogram for that
        // bucket must drop to zero and no new bucket entry must appear.
        ValueHash256 acc = Keccak.Compute("acc").ValueHash256;
        int bucket = VisitorCounters.ComputeSlotBucket(10);

        long[] seedHist = new long[CumulativeTrieStats.SlotHistogramLength];
        seedHist[bucket] = 1;
        StateCompositionStateHolder holder = HolderWithBaseline(
            TestDataBuilders.EmptyBaseline(histogram: seedHist),
            slotCountByAddress: new Dictionary<ValueHash256, long> { [acc] = 10 });

        TrieDiff diff = DiffWithPayloads(slotCountChanges:
        [
            new SlotCountChange(acc, -10),
        ]);
        CumulativeTrieStats updated = holder.ApplyIncrementalDiffAndUpdate(
            diff, blockNumber: 2, stateRoot: AnyRoot, codeSizeLookup: _ => 0);

        long totalContracts = updated.SlotCountHistogram.Sum();
        Assert.That(totalContracts, Is.Zero,
            "emptied contract must leave the histogram — no contracts tracked");
    }

    [Test]
    public void ApplyIncrementalDiff_BaselineHistogramUntouchedByDiff()
    {
        // Copy-on-write invariant: the ImmutableArray<long> captured before the diff
        // must not observe any in-place mutation after ApplyIncrementalDiffAndUpdate
        // writes the new value.
        ValueHash256 acc = Keccak.Compute("acc").ValueHash256;

        long[] seed = new long[CumulativeTrieStats.SlotHistogramLength];
        seed[2] = 5;
        StateCompositionStateHolder holder = HolderWithBaseline(TestDataBuilders.EmptyBaseline(histogram: seed));
        ImmutableArray<long> capturedBaseline = holder.IncrementalStats.SlotCountHistogram;

        TrieDiff diff = DiffWithPayloads(slotCountChanges:
        [
            new SlotCountChange(acc, 100),
        ]);
        holder.ApplyIncrementalDiffAndUpdate(diff, 2, AnyRoot, _ => 0);

        Assert.That(capturedBaseline[2], Is.EqualTo(5),
            "baseline histogram must not alias the post-diff buffer");
    }
}
