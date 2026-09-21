// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotCompactorTests
{
    private readonly PbtResourcePool _pool = new(new PbtConfig());
    private static readonly PbtConfig Config = new() { CompactSize = 16 };

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void Compact_PreservesNewestCanonicalLeafAndGroupAfterSourcesAreDisposed(bool tombstone, bool storagePathFirst)
    {
        if (storagePathFirst) AssertCompactedCanonicalPaths(tombstone, new PbtStorageNodePath([], 0), new PbtNodePath([], 0));
        else AssertCompactedCanonicalPaths(tombstone, new PbtNodePath([], 0), new PbtStorageNodePath([], 0));
    }

    private void AssertCompactedCanonicalPaths<TPath, TAlternatePath>(bool tombstone, TPath groupKey, TAlternatePath alternateGroupKey)
        where TPath : struct, IPbtNodePath<TPath>
        where TAlternatePath : struct, IPbtNodePath<TAlternatePath>
    {
        PbtStorageTreeKey key = PbtStateKey.Storage(TestItem.AddressA, 64);
        TrackingMemoryProvider memoryProvider = new();
        PbtSnapshotContent older = new();
        PbtSnapshotContent newer = new();
        older.SetSlot(key, EvmWordSlot.FromStripped(TestItem.KeccakA.Bytes));
        newer.SetSlot(key, EvmWordSlot.FromStripped(TestItem.KeccakB.Bytes));
        byte[] expected;
        using (RefCountingMemory olderPayload = CreateStorageLeafGroup())
        using (RefCountingMemory newerPayload = CreateStorageLeafGroup())
        {
            older.SetNodeGroup(groupKey, olderPayload);
            Assert.That(older.TryGetNodeGroup(alternateGroupKey, out RefCountingMemory? original), Is.True);
            using (original)
            {
                older.SetNodeGroup(alternateGroupKey, newerPayload);
                Assert.That(older.TryGetNodeGroup(groupKey, out RefCountingMemory? replacement), Is.True);
                using (replacement)
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(older.NodeGroupCount, Is.EqualTo(1));
                    Assert.That(original, Is.SameAs(olderPayload));
                    Assert.That(replacement, Is.SameAs(newerPayload));
                }
            }
            older.SetNodeGroup(groupKey, olderPayload);
            newer.SetNodeGroup(alternateGroupKey, tombstone ? null : newerPayload);
            expected = newerPayload.GetSpan().ToArray();
        }
        PbtSnapshot compacted;
        using (PbtSnapshotPooledList chain = new(2))
        {
            chain.Add(new PbtSnapshot(StateId.PreGenesis, new StateId(1, default), default, older, _pool, PbtResourcePool.Usage.MainBlockProcessing));
            chain.Add(new PbtSnapshot(new StateId(1, default), new StateId(2, default), default, newer, _pool, PbtResourcePool.Usage.MainBlockProcessing));
            compacted = NewCompactor().Compact(chain);
        }
        using (compacted)
        {
            bool found = compacted.Content.TryGetNodeGroup(groupKey, out RefCountingMemory? payload);
            using RefCountingMemory? payloadLease = payload;
            bool alternateFound = compacted.Content.TryGetNodeGroup(alternateGroupKey, out RefCountingMemory? alternatePayload);
            using RefCountingMemory? alternatePayloadLease = alternatePayload;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(compacted.Content.GetSlot(key), Is.EqualTo(EvmWordSlot.FromStripped(TestItem.KeccakB.Bytes)));
                Assert.That(compacted.Content.NodeGroupCount, Is.EqualTo(1));
                Assert.That(found, Is.True);
                Assert.That(alternateFound, Is.True);
                Assert.That(alternatePayload, Is.SameAs(payload));
                Assert.That(payload?.Memory.ToArray(), Is.EqualTo(tombstone ? null : expected));
            }
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);

        RefCountingMemory CreateStorageLeafGroup()
        {
            PbtTraversalPath path = PbtTraversalPath.FromPath(stackalloc byte[PbtStorageTreeKey.MaxLength], groupKey);
            using PbtNodeGroupWriter<TPath> writer = new(groupKey.BitDepth, memoryProvider, true);
            writer.Write(path, PbtFourLevelGroupGeometry.RootPosition, PbtNodeCodec.EncodeLeaf(key));
            return writer.Detach(default)!;
        }
    }

    [TestCase(7u, false)]
    [TestCase(7u, true)]
    [TestCase(1000u, false)]
    [TestCase(1000u, true)]
    public void Compact_preserves_clear_ordering_and_whole_typed_values(uint slot, bool clearLast)
    {
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        PbtStorageTreeKey key = PbtStateKey.Storage(TestItem.AddressA, slot);
        PbtStorageTreeKey otherSlot = PbtStateKey.Storage(TestItem.AddressA, slot + 1);
        PbtStorageTreeKey otherAddress = PbtStateKey.Storage(TestItem.AddressB, slot);
        EvmWord original = EvmWordSlot.FromStripped(Bytes.FromHexString("01"));
        EvmWord replacement = EvmWordSlot.FromStripped(Bytes.FromHexString("02"));
        CodeInfo code = new(Bytes.FromHexString("6001600055"));
        Account account = Build.An.Account.WithNonce(7).WithBalance(9).WithStorageRoot(TestItem.KeccakB).WithCode(code.Code.ToArray()).TestObject;
        PbtSnapshotContent older = new();
        older.Accounts[addressHash] = Build.An.Account.TestObject;
        older.SetSlot(key, original);
        older.SetSlot(otherSlot, original);
        older.SetSlot(otherAddress, original);
        PbtSnapshotContent clearing = new();
        clearing.ClearStorage(addressHash);
        PbtSnapshotContent writing = new();
        writing.SetSlot(key, replacement);
        ISlotRun writtenRun = writing.Storages[SlotRun.RunKey(key)];
        writing.Accounts[addressHash] = account;
        writing.Codes[account.CodeHash.ValueHash256] = code;
        PbtSnapshot compacted;
        using (PbtSnapshotPooledList chain = new(3))
        {
            chain.Add(new PbtSnapshot(StateId.PreGenesis, new StateId(1, default), default, older, _pool, PbtResourcePool.Usage.MainBlockProcessing));
            chain.Add(new PbtSnapshot(new StateId(1, default), new StateId(2, default), default, clearLast ? writing : clearing, _pool, PbtResourcePool.Usage.MainBlockProcessing));
            chain.Add(new PbtSnapshot(new StateId(2, default), new StateId(3, default), default, clearLast ? clearing : writing, _pool, PbtResourcePool.Usage.MainBlockProcessing));
            compacted = NewCompactor().Compact(chain);
        }
        using (compacted)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(compacted.Content.Accounts[addressHash], Is.SameAs(account));
            Assert.That(compacted.Content.Codes[account.CodeHash.ValueHash256], Is.SameAs(code));
            Assert.That(compacted.Content.SelfDestructedStorageAddresses.ContainsKey(addressHash), Is.True);
            Assert.That(compacted.Content.TryGetSlot(key, out _), Is.EqualTo(!clearLast));
            if (!clearLast) Assert.That(compacted.Content.GetSlot(key), Is.EqualTo(replacement));
            if (!clearLast) Assert.That(compacted.Content.Storages[SlotRun.RunKey(key)], Is.Not.SameAs(writtenRun), "compaction copies runs, the source layer keeps its own");
            // The rewritten run is whole, so the older layer's neighbouring slot does not survive the rewrite.
            Assert.That(compacted.Content.GetSlot(otherSlot), Is.EqualTo(default(EvmWord)));
            Assert.That(compacted.Content.GetSlot(otherAddress), Is.EqualTo(original));
        }
    }

    private PbtSnapshotCompactor NewCompactor() => new(_pool, new PbtCompactionSchedule(new Nethermind.Db.MemDb(), Config, Nethermind.Logging.LimboLogs.Instance), new PbtSnapshotRepository(), Config);
}
