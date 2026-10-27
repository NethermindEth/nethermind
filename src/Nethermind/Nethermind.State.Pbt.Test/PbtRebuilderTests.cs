// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Persistence.TrieNodeLog;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtRebuilderTests
{
    private PbtConfig Config => new();

    private static List<RebuildEntry> BuildFixture(Dictionary<string, byte[]> model, PbtRocksDbPersistence target)
    {
        byte[] bigCode = new byte[8000];
        for (int i = 0; i < bigCode.Length; i += 10) bigCode[i] = 0x63; // PUSH4, to exercise the chunk PUSHDATA offsets
        byte[] smallCode = Bytes.FromHexString("0x60016002");

        List<RebuildEntry> leaves = [];
        using IPbtPersistence.IWriteBatch stagingBatch = target.CreateStagingWriteBatch();

        void AddAccount(Address address, ulong nonce, in UInt256 balance, byte[]? code)
        {
            PbtReferenceModel.SetAccount(model, address, nonce, balance, code);
            Account account = code is { Length: > 0 }
                ? new Account(nonce, balance).WithChangedCodeHash(Keccak.Compute(code))
                : new Account(nonce, balance);
            PbtTestLeaves.AddAccount(leaves, address, account, code is { Length: > 0 } ? code : null);
            stagingBatch.SetAccount(Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes), PbtAccount.From(account, code is { Length: > 0 } ? new CodeInfo(code) : null));
            if (code is { Length: > 0 }) stagingBatch.SetCode(account.CodeHash.ValueHash256, new CodeInfo(code));
        }

        void AddSlot(Address address, in UInt256 slot, in UInt256 value)
        {
            PbtReferenceModel.SetSlot(model, address, slot, value);
            PbtTestLeaves.AddSlot(leaves, address, slot, value);
            stagingBatch.SetSlot(PbtTestLeaves.SlotKey(address, slot), value);
        }

        AddAccount(TestItem.AddressA, 1, 100, null);                  // EOA
        AddAccount(TestItem.AddressB, 0, 42, bigCode);               // multi-group code contract
        AddSlot(TestItem.AddressB, 5, 0xAB);                        // header-region slot (< 64)
        AddSlot(TestItem.AddressB, 70, 0x07);                       // storage-zone slot (>= 64)
        AddSlot(TestItem.AddressB, 1000, 0x1234);
        AddAccount(TestItem.AddressC, 2, 7, smallCode);             // single-chunk contract
        AddSlot(TestItem.AddressC, 3, 0x99);
        AddAccount(TestItem.AddressD, 0, 0, bigCode);
        AddAccount(TestItem.AddressE, 0, 0, null);
        byte[] delegation = Bytes.FromHexString("ef01000000000000000000000000000000000000000001");
        AddAccount(TestItem.AddressF, 1, 0, delegation);
        AddAccount(TestItem.Addresses[6], 1, 0, delegation);

        stagingBatch.Commit();
        return leaves;
    }

    private static ValueHash256 Rebuild(List<RebuildEntry> leaves, PbtRocksDbPersistence target, ILogManager logManager, int windowSize)
    {
        PbtConfig config = new();
        using IEnumerator<RebuildEntry> source = leaves.GetEnumerator();
        return new PbtRebuilder(target, config, config.FoldConcurrency, logManager).Rebuild(source, windowSize, CancellationToken.None);
    }

    [Test]
    public void Rebuild_matches_reference_root_across_windows([Values(0, 1, 3)] int windowSize)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtConfig config = Config;
        PbtRocksDbPersistence target = new(db, config, NullTrieNodeLog.Instance);
        Dictionary<string, byte[]> model = [];
        List<RebuildEntry> leaves = BuildFixture(model, target);
        leaves.Sort(static (left, right) => left.Key.CompareTo(right.Key));

        TestLogger log = new();
        ILogManager logManager = new OneLoggerLogManager(new ILogger(log));

        ValueHash256 root = Rebuild(leaves, target, logManager, windowSize);

        using PbtNodeGroupStore incrementalStore = new();
        ValueHash256 incrementalRoot = default;
        using IPbtPersistence.IReader reader = target.CreateReader();
        foreach ((PbtVariableTreeKey key, ValueHash256 value) in leaves)
        {
            incrementalRoot = incrementalStore.Fold(incrementalRoot, [(key.Bytes.ToArray(), value.ToByteArray())],
                PbtTreeHarness.DefaultFanOut);
        }

        int physicalNodeCount = 0;
        foreach (PbtStorageNodePath groupKey in PbtStoreTestExtensions.PersistedNodeGroupKeys(db))
        {
            using RefCountingMemory payload = reader.GetNodeGroup(groupKey)!;
            physicalNodeCount += PbtStoreTestExtensions.ReadGroup(groupKey, payload.GetSpan()).Nodes().Count;
        }
        Assert.That(physicalNodeCount, Is.LessThan(incrementalStore.EnumerateRecords().Count));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)), "rebuilt root must match the EIP reference tree");
            Assert.That(root, Is.EqualTo(incrementalRoot), "incremental replay and windowed rebuild must have the same root");
            PbtStoreTestExtensions.AssertSameGroups(db, reader, incrementalStore, "incremental replay and windowed rebuild must have the exact same group keys and payloads");
            Assert.That(log.LogList, Has.Some.Contains("PBT rebuild complete").And.Some.Contains($"{leaves.Count} received leaves"));
        }
    }

    [Test]
    public void Rebuild_empty_source_produces_empty_tree()
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config, NullTrieNodeLog.Instance);
        ValueHash256 root = Rebuild([], target, LimboLogs.Instance, 0);

        Assert.That(root, Is.EqualTo(default(ValueHash256)), "an empty tree is 32 zero bytes");
    }

    [Test]
    public async Task Rebuild_commits_leaf_windows_without_publishing([Values(1, 3)] int windowSize, [Values] SourceEnd end)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config, NullTrieNodeLog.Instance);
        using BlockingCollection<RebuildEntry> pending = new();
        using CancellationTokenSource cancellation = new();
        DrainedLeafEnumerator source = new(pending, windowSize, cancellation.Token);
        Task<ValueHash256> rebuilding = Task.Run(() => new PbtRebuilder(target, Config, Config.FoldConcurrency, LimboLogs.Instance)
            .Rebuild(source, windowSize, cancellation.Token));
        using PbtNodeGroupStore expectedStore = new();
        ValueHash256 expectedRoot = default;
        ValueHash256 rebuiltRoot = default;
        Exception? failure = null;
        try
        {
            for (int index = 0; index < 2; index++)
            {
                PbtVariableTreeKey key = (PbtVariableTreeKey)Eip8297KeyDerivation.OverflowCodeKey(TestItem.KeccakB.ValueHash256.Bytes, 256 + index);
                for (int repeat = 0; repeat < windowSize; repeat++) pending.Add(new(key, TestItem.KeccakC.ValueHash256));
                await source.Drained[index].Task.WaitAsync(TimeSpan.FromSeconds(10));

                expectedRoot = expectedStore.Fold(expectedRoot, [(key.Bytes.ToArray(), TestItem.KeccakC.BytesToArray())]);
                using IPbtPersistence.IReader reader = target.CreateReader();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(rebuilding.IsCompleted, Is.False);
                    Assert.That(reader.CurrentState, Is.EqualTo(StateId.PreGenesis));
                    Assert.That(target.IsValid, Is.False);
                    PbtStoreTestExtensions.AssertSameGroups(db, reader, expectedStore);
                }
            }

            switch (end)
            {
                case SourceEnd.Complete:
                    pending.CompleteAdding();
                    break;
                case SourceEnd.Cancel:
                    await cancellation.CancelAsync();
                    break;
                case SourceEnd.Fail:
                    source.Failure = new InvalidDataException("source failed");
                    pending.CompleteAdding();
                    break;
            }
            try { rebuiltRoot = await rebuilding.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception exception) { failure = exception; }
        }
        finally
        {
            await cancellation.CancelAsync();
            pending.CompleteAdding();
        }
        using IPbtPersistence.IReader completedReader = target.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            if (end == SourceEnd.Complete)
            {
                Assert.That(failure, Is.Null);
                Assert.That(rebuiltRoot, Is.EqualTo(expectedRoot));
            }
            else
            {
                Assert.That(failure, end == SourceEnd.Cancel ? Is.InstanceOf<OperationCanceledException>() : Is.TypeOf<InvalidDataException>());
            }
            Assert.That(completedReader.CurrentState, Is.EqualTo(StateId.PreGenesis));
            Assert.That(target.IsValid, Is.False);
            PbtStoreTestExtensions.AssertSameGroups(db, completedReader, expectedStore);
        }
    }

    public enum SourceEnd { Complete, Cancel, Fail }

    /// <summary>Hands out the leaves added to <paramref name="source"/>, signalling each time a whole batch has been read and it waits for more.</summary>
    private sealed class DrainedLeafEnumerator(BlockingCollection<RebuildEntry> source, int batchSize, CancellationToken cancellationToken) : IEnumerator<RebuildEntry>
    {
        private int _consumed;
        public Exception? Failure { get; set; }
        public TaskCompletionSource[] Drained { get; } =
            [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        public RebuildEntry Current { get; private set; }
        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (!source.TryTake(out RebuildEntry entry))
            {
                if (_consumed != 0 && _consumed % batchSize == 0) Drained[_consumed / batchSize - 1].TrySetResult();
                if (!source.TryTake(out entry, Timeout.Infinite, cancellationToken))
                {
                    if (Failure is not null) throw Failure;
                    return false;
                }
            }
            _consumed++;
            Current = entry;
            return true;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose() { }
    }
}
