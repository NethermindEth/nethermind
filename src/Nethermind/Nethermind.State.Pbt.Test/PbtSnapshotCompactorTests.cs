// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotCompactorTests
{
    private readonly PbtResourcePool _pool = new(new PbtConfig());
    private static readonly PbtConfig Config = new() { CompactSize = 16 };

    [Test]
    public void Compact_PreservesNewestCanonicalLeafAndGroupAfterSourcesAreDisposed([Values] bool tombstone)
    {
        PbtNodePath groupKey = new([], 0);
        PbtStorageNodePath alternateGroupKey = new([], 0);
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
            newer.SetNodeGroup(alternateGroupKey, tombstone ? null : newerPayload);
            expected = newerPayload.GetSpan().ToArray();
        }
        using (PbtSnapshot compacted = Compact(older, newer))
        {
            bool found = compacted.Content.TryGetNodeGroup(groupKey, out RefCountingMemory? payload);
            using RefCountingMemory? payloadLease = payload;
            bool alternateFound = compacted.Content.TryGetNodeGroup(alternateGroupKey, out RefCountingMemory? alternatePayload);
            using RefCountingMemory? alternatePayloadLease = alternatePayload;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(compacted.Content.GetSlot(key), Is.EqualTo(EvmWordSlot.FromStripped(TestItem.KeccakB.Bytes)));
                Assert.That(compacted.Content.NodeGroupCount(), Is.EqualTo(1));
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
            using PbtNodeGroupWriter<PbtNodePath> writer = new(groupKey.BitDepth, memoryProvider);
            writer.Write(path, PbtFourLevelGroupGeometry.RootPosition, PbtTreeHarness.EncodeLeaf(key));
            return writer.Detach(default, ushort.MaxValue)!;
        }
    }

    [Test]
    public void Compact_preserves_clear_ordering_and_whole_typed_values([Values(7u, 1000u)] uint slot, [Values] bool clearLast)
    {
        ValueHash256 addressHash = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        PbtStorageTreeKey key = PbtStateKey.Storage(TestItem.AddressA, slot);
        PbtStorageTreeKey otherSlot = PbtStateKey.Storage(TestItem.AddressA, slot + 1);
        PbtStorageTreeKey otherAddress = PbtStateKey.Storage(TestItem.AddressB, slot);
        EvmWord original = EvmWordSlot.FromStripped(Bytes.FromHexString("01"));
        EvmWord replacement = EvmWordSlot.FromStripped(Bytes.FromHexString("02"));
        CodeInfo code = new(Bytes.FromHexString("6001600055"));
        Account account = Build.An.Account.WithNonce(7).WithBalance(9).WithStorageRoot(TestItem.KeccakB).WithCode(code.Code.ToArray()).TestObject;
        PbtSnapshotContent older = new();
        older.Accounts[addressHash] = Build.An.Account.TestObject.ToPbtAccount();
        older.SetSlot(key, original);
        older.SetSlot(otherSlot, original);
        older.SetSlot(otherAddress, original);
        PbtSnapshotContent clearing = new();
        clearing.ClearStorage(addressHash);
        PbtSnapshotContent writing = new();
        writing.SetSlot(key, replacement);
        PackedSlotRun writtenRun = writing.Storages[SlotRun.RunKey(key)];
        writing.Accounts[addressHash] = PbtAccount.From(account, code);
        writing.Codes[account.CodeHash.ValueHash256] = code;
        using (PbtSnapshot compacted = Compact(older, clearLast ? writing : clearing, clearLast ? clearing : writing))
        using (Assert.EnterMultipleScope())
        {
            Assert.That(compacted.Content.Accounts[addressHash], Is.EqualTo(PbtAccount.From(account, code)));
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

    private PbtSnapshot Compact(params PbtSnapshotContent[] layersOldestFirst)
    {
        using PbtSnapshotPooledList chain = PbtSnapshotBundleTestExtensions.Chain(_pool, layersOldestFirst);
        return NewCompactor().Compact(chain);
    }

    private PbtSnapshotCompactor NewCompactor() => new(_pool, new PbtCompactionSchedule(new Nethermind.Db.MemDb(), Config, Nethermind.Logging.LimboLogs.Instance), new PbtSnapshotRepository(new Nethermind.Monitoring.Config.MetricsConfig()), Config);
}
