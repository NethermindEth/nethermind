// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
        using IPbtPersistence.IWriteBatch stagingBatch = target.CreateStagingWriteBatch(WriteFlags.None);

        void AddAccount(Address address, ulong nonce, in UInt256 balance, byte[]? code)
        {
            PbtReferenceModel.SetAccount(model, address, nonce, balance, code);
            Account account = code is { Length: > 0 }
                ? new Account(nonce, balance).WithChangedCodeHash(Keccak.Compute(code))
                : new Account(nonce, balance);
            PbtTestLeaves.AddAccount(leaves, address, account, code is { Length: > 0 } ? code : null);
            stagingBatch.SetAccount(PbtStateKey.AddressKeyHash(address), PbtAccount.From(account, code is { Length: > 0 } ? new CodeInfo(code) : null));
            if (code is { Length: > 0 }) stagingBatch.SetCode(account.CodeHash.ValueHash256, new CodeInfo(code));
        }

        void AddSlot(Address address, in UInt256 slot, in UInt256 value)
        {
            PbtReferenceModel.SetSlot(model, address, slot, value);
            PbtTestLeaves.AddSlot(leaves, address, slot, value);
            stagingBatch.SetSlot(PbtTestLeaves.SlotKey(address, slot), EvmWordSlot.FromStripped(value.ToBigEndian()));
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

    private static async Task<ValueHash256> Rebuild(
        List<RebuildEntry> leaves,
        int chunkSize,
        StateId targetState,
        PbtRocksDbPersistence target,
        ILogManager logManager,
        int windowSize)
    {
        PbtRebuilder rebuilder = new(target, new PbtConfig(), logManager);
        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateUnbounded<ArrayPoolList<RebuildEntry>>();
        for (int offset = 0; offset < leaves.Count; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, leaves.Count - offset);
            ArrayPoolList<RebuildEntry> chunk = new(count);
            for (int index = 0; index < count; index++) chunk.Add(leaves[offset + index]);
            channel.Writer.TryWrite(chunk);
        }
        channel.Writer.Complete();

        return await rebuilder.Rebuild(channel.Reader, targetState, CancellationToken.None, windowSize, expectedRoot: null, publishAfter: Task.CompletedTask);
    }

    [TestCase(1, 3)]
    [TestCase(3, 1)]
    [TestCase(int.MaxValue, 0)]
    public async Task Rebuild_matches_reference_root_across_channel_chunks(int chunkSize, int windowSize)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtConfig config = Config;
        PbtRocksDbPersistence target = new(db, config, NullTrieNodeLog.Instance);
        Dictionary<string, byte[]> model = [];
        List<RebuildEntry> leaves = BuildFixture(model, target);
        Random random = new(42);
        for (int i = leaves.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (leaves[i], leaves[j]) = (leaves[j], leaves[i]);
        }

        TestLogger log = new();
        ILogManager logManager = new OneLoggerLogManager(new ILogger(log));

        // the header root the source claims is unrelated to the tree root the fold produces, so the
        // two must be recorded separately rather than one standing in for the other
        StateId targetState = new(7, TestItem.KeccakA.ValueHash256);
        ValueHash256 root = await Rebuild(leaves, chunkSize, targetState, target, logManager, windowSize);

        using PbtNodeGroupStore incrementalStore = new();
        ValueHash256 incrementalRoot = default;
        using IPbtPersistence.IReader reader = target.CreateReader();
        foreach ((PbtVariableTreeKey key, ValueHash256 value) in leaves)
        {
            incrementalRoot = incrementalStore.Fold(incrementalRoot, [(key.Bytes.ToArray(), value.ToByteArray())],
                PbtTreeHarness.DefaultFanOut, null);
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
            Assert.That(reader.CurrentState, Is.EqualTo(targetState), "persisted state pointer must advance to the rebuilt state");
            Assert.That(reader.CurrentRoot, Is.EqualTo(root), "and record the tree root beside it");
            Assert.That(log.LogList, Has.Some.Contains("PBT rebuild complete").And.Some.Contains($"{leaves.Count} received leaves"));
        }
    }

    [Test]
    public async Task Rebuild_empty_source_produces_empty_tree()
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config, NullTrieNodeLog.Instance);
        StateId targetState = new(3, TestItem.KeccakA.ValueHash256);
        ValueHash256 root = await Rebuild([], 1, targetState, target, LimboLogs.Instance, 0);

        Assert.That(root, Is.EqualTo(default(ValueHash256)), "an empty tree is 32 zero bytes");
        using IPbtPersistence.IReader reader = target.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(targetState));
    }

    [Test]
    public async Task Rebuild_commits_leaf_windows_but_publishes_only_on_completion([Values(1, 3)] int windowSize, [Values] SourceEnd end)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config, NullTrieNodeLog.Instance);
        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateBounded<ArrayPoolList<RebuildEntry>>(1);
        DrainedChunkReader source = new(channel.Reader);
        StateId targetState = new(7, TestItem.KeccakA.ValueHash256);
        using CancellationTokenSource cancellation = new();
        Task<ValueHash256> rebuilding = new PbtRebuilder(target, Config, LimboLogs.Instance)
            .Rebuild(source, targetState, cancellation.Token, windowSize, expectedRoot: null, publishAfter: Task.CompletedTask);
        using PbtNodeGroupStore expectedStore = new();
        ValueHash256 expectedRoot = default;
        ValueHash256 rebuiltRoot = default;
        Exception? failure = null;
        try
        {
            for (int index = 0; index < 2; index++)
            {
                PbtVariableTreeKey key = (PbtVariableTreeKey)PbtStateKey.Code(TestItem.KeccakB.ValueHash256, 256 + index);
                ArrayPoolList<RebuildEntry> chunk = new(windowSize);
                for (int repeat = 0; repeat < windowSize; repeat++) chunk.Add(new(key, TestItem.KeccakC.ValueHash256));
                await channel.Writer.WriteAsync(chunk);
                await source.Drained[index].Task.WaitAsync(TimeSpan.FromSeconds(10));

                expectedRoot = expectedStore.Fold(expectedRoot, [(key.Bytes.ToArray(), TestItem.KeccakC.BytesToArray())]);
                using IPbtPersistence.IReader reader = target.CreateReader();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(rebuilding.IsCompleted, Is.False);
                    Assert.That(reader.CurrentState, Is.EqualTo(StateId.PreGenesis));
                    Assert.That(target.IsValid, Is.False);
                    PbtStoreTestExtensions.AssertSameGroups(db, reader, expectedStore);
                    Assert.Throws<ObjectDisposedException>(() => chunk.AsSpan());
                }
            }

            switch (end)
            {
                case SourceEnd.Complete:
                    channel.Writer.Complete();
                    break;
                case SourceEnd.Cancel:
                    await cancellation.CancelAsync();
                    break;
                case SourceEnd.Fail:
                    channel.Writer.Complete(new InvalidDataException("source failed"));
                    break;
            }
            try { rebuiltRoot = await rebuilding.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception exception) { failure = exception; }
        }
        finally
        {
            await cancellation.CancelAsync();
            channel.Writer.TryComplete();
        }
        using IPbtPersistence.IReader completedReader = target.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            if (end == SourceEnd.Complete)
            {
                Assert.That(failure, Is.Null);
                Assert.That(rebuiltRoot, Is.EqualTo(expectedRoot));
                Assert.That(completedReader.CurrentState, Is.EqualTo(targetState));
                Assert.That(target.IsValid, Is.True);
            }
            else
            {
                Assert.That(failure, end == SourceEnd.Cancel ? Is.InstanceOf<OperationCanceledException>() : Is.TypeOf<InvalidDataException>());
                Assert.That(completedReader.CurrentState, Is.EqualTo(StateId.PreGenesis));
                Assert.That(target.IsValid, Is.False);
                PbtStoreTestExtensions.AssertSameGroups(db, completedReader, expectedStore);
            }
        }
    }

    public enum SourceEnd { Complete, Cancel, Fail }

    private sealed class DrainedChunkReader(ChannelReader<ArrayPoolList<RebuildEntry>> source) : ChannelReader<ArrayPoolList<RebuildEntry>>
    {
        private int _readChunks;
        public TaskCompletionSource[] Drained { get; } =
            [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];

        public override bool TryRead(out ArrayPoolList<RebuildEntry> item)
        {
            if (source.TryRead(out item!))
            {
                _readChunks++;
                return true;
            }
            if (_readChunks != 0) Drained[_readChunks - 1].TrySetResult();
            return false;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            source.WaitToReadAsync(cancellationToken);
    }
}
