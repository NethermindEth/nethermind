// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtRebuilderTests
{
    private PbtConfig Config => new();

    private static List<RebuildEntry> BuildFixture(Dictionary<string, byte[]> model, PbtRocksDbPersistence target)
    {
        // > 128 chunks (3968 bytes) so overflow chunks land in the content-addressed code zone
        byte[] bigCode = new byte[5000];
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
            stagingBatch.SetAccount(PbtKeyDerivation.AddressKeyHash(address), account);
            if (code is { Length: > 0 }) stagingBatch.SetCode(account.CodeHash.ValueHash256, new CodeInfo(code));
        }

        void AddSlot(Address address, in UInt256 slot, in UInt256 value)
        {
            PbtReferenceModel.SetSlot(model, address, slot, value);
            PbtTestLeaves.AddSlot(leaves, address, slot, value);
            stagingBatch.SetSlot(PbtStateKey.Storage(address, slot), EvmWordSlot.FromStripped(value.ToBigEndian()));
        }

        AddAccount(TestItem.AddressA, 1, 100, null);                  // EOA
        AddAccount(TestItem.AddressB, 0, 42, bigCode);               // overflow-code contract
        AddSlot(TestItem.AddressB, 5, 0xAB);                        // header-region slot (< 64)
        AddSlot(TestItem.AddressB, 70, 0x07);                       // storage-zone slot (>= 64)
        AddSlot(TestItem.AddressB, 1000, 0x1234);
        AddAccount(TestItem.AddressC, 2, 7, smallCode);             // small contract, no overflow
        AddSlot(TestItem.AddressC, 3, 0x99);
        AddAccount(TestItem.AddressD, 0, 0, bigCode);
        AddAccount(TestItem.AddressE, 0, 0, null);

        stagingBatch.Commit();
        return leaves;
    }

    private static async Task<ValueHash256> Rebuild(
        List<RebuildEntry> leaves,
        int chunkSize,
        StateId targetState,
        PbtRocksDbPersistence target,
        ILogManager? logManager = null,
        int windowSize = 0)
    {
        PbtRebuilder rebuilder = new(target, logManager ?? LimboLogs.Instance);
        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateUnbounded<ArrayPoolList<RebuildEntry>>();
        for (int offset = 0; offset < leaves.Count; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, leaves.Count - offset);
            ArrayPoolList<RebuildEntry> chunk = new(count);
            for (int index = 0; index < count; index++) chunk.Add(leaves[offset + index]);
            channel.Writer.TryWrite(chunk);
        }
        channel.Writer.Complete();

        return await rebuilder.Rebuild(channel.Reader, targetState, CancellationToken.None, windowSize);
    }

    [TestCase(1, 3)]
    [TestCase(3, 1)]
    [TestCase(int.MaxValue, 0)]
    public async Task Rebuild_matches_reference_root_across_channel_chunks(int chunkSize, int windowSize)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);
        Dictionary<string, byte[]> model = [];
        List<RebuildEntry> leaves = BuildFixture(model, target);

        // the header root the source claims is unrelated to the tree root the fold produces, so the
        // two must be recorded separately rather than one standing in for the other
        StateId targetState = new(7, TestItem.KeccakA.ValueHash256);
        ValueHash256 root = await Rebuild(leaves, chunkSize, targetState, target, windowSize: windowSize);

        using PbtNodeGroupStore incrementalStore = new();
        ValueHash256 incrementalRoot = default;
        using IPbtPersistence.IReader reader = target.CreateReader();
        foreach ((PbtStorageFullKey key, ValueHash256 value) in leaves)
        {
            using PbtWriteBatchBuilder<PbtStorageFullKey> incrementalChange = new(0);
            incrementalChange.Set(key, value);
            using PbtWriteBatch<PbtStorageFullKey> preparedChange = incrementalChange.Build();
            incrementalRoot = TrieUpdater.UpdateRoot(incrementalStore, incrementalRoot, preparedChange);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)), "rebuilt root must match the EIP reference tree");
            Assert.That(root, Is.EqualTo(incrementalRoot), "incremental replay and windowed rebuild must have the same root");
            Assert.That(CanonicalGroups(reader.EnumerateNodeGroupKeys(), reader.GetNodeGroup),
                Is.EqualTo(CanonicalGroups(incrementalStore.EnumerateNodeGroupKeys(), incrementalStore.GetNodeGroup)),
                "incremental replay and windowed rebuild must have the exact same group keys and payloads");
            Assert.That(reader.CurrentState, Is.EqualTo(targetState), "persisted state pointer must advance to the rebuilt state");
            Assert.That(reader.CurrentRoot, Is.EqualTo(root), "and record the tree root beside it");
        }
    }

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(int.MaxValue)]
    public async Task Rebuild_is_independent_of_entry_and_chunk_order(int chunkSize)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);
        Dictionary<string, byte[]> model = [];
        List<RebuildEntry> leaves = BuildFixture(model, target);

        Random random = new(42);
        for (int i = leaves.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (leaves[i], leaves[j]) = (leaves[j], leaves[i]);
        }

        ValueHash256 root = await Rebuild(leaves, chunkSize, new StateId(7, TestItem.KeccakA.ValueHash256), target);

        Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
    }

    [Test]
    public async Task Rebuild_logs_completion()
    {
        List<string> messages = [];
        InterfaceLogger underlyingLogger = Substitute.For<InterfaceLogger>();
        underlyingLogger.IsInfo.Returns(true);
        underlyingLogger.Info(Arg.Do<string>(messages.Add));
        ILogger logger = new(underlyingLogger);
        ILogManager logManager = Substitute.For<ILogManager>();
        logManager.GetClassLogger<PbtRebuilder>().Returns(logger);
        logManager.GetClassLogger<ProgressLogger>().Returns(logger);

        List<RebuildEntry> leaves = [];
        PbtTestLeaves.AddAccount(leaves, TestItem.AddressA, new Account(1, 100), null);
        PbtTestLeaves.AddSlot(leaves, TestItem.AddressA, 0, 1);

        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);
        using (IPbtPersistence.IWriteBatch stagingBatch = target.CreateStagingWriteBatch(WriteFlags.None))
        {
            stagingBatch.SetAccount(PbtKeyDerivation.AddressKeyHash(TestItem.AddressA), new Account(1, 100));
            stagingBatch.SetSlot(PbtStateKey.Storage(TestItem.AddressA, 0), EvmWordSlot.FromStripped(Bytes.FromHexString("0x01")));
            stagingBatch.Commit();
        }
        await Rebuild(leaves, int.MaxValue, new StateId(7, TestItem.KeccakA.ValueHash256), target, logManager);

        Assert.That(messages, Has.Some.Contains("PBT rebuild complete").And.Some.Contains("3 received leaves"));
    }

    [Test]
    public async Task Rebuild_empty_source_produces_empty_tree()
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);
        PbtRebuilder rebuilder = new(target, LimboLogs.Instance);

        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateUnbounded<ArrayPoolList<RebuildEntry>>();
        channel.Writer.Complete();

        StateId targetState = new(3, TestItem.KeccakA.ValueHash256);
        ValueHash256 root = await rebuilder.Rebuild(channel.Reader, targetState, CancellationToken.None);

        Assert.That(root, Is.EqualTo(default(ValueHash256)), "an empty tree is 32 zero bytes");
        using IPbtPersistence.IReader reader = target.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(targetState));
    }

    [Test]
    public async Task Rebuild_counts_shared_code_references_across_leaf_windows([Values(1, 3, 0)] int windowSize)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);
        Dictionary<string, byte[]> model = [];
        List<RebuildEntry> leaves = BuildFixture(model, target);

        ValueHash256 root = await Rebuild(leaves, 5, new StateId(7, TestItem.KeccakA.ValueHash256), target, windowSize: windowSize);

        using IPbtPersistence.IReader reader = target.CreateReader();
        Account sharedCodeAccount = PbtTestLeaves.ReadAccount(reader, TestItem.AddressB)!;
        Account uniqueCodeAccount = PbtTestLeaves.ReadAccount(reader, TestItem.AddressC)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(reader.GetCodeReference(sharedCodeAccount.CodeHash.ValueHash256), Is.EqualTo(2));
            Assert.That(reader.GetCodeReference(uniqueCodeAccount.CodeHash.ValueHash256), Is.EqualTo(1));
            Assert.That(reader.GetCodeReference(Keccak.OfAnEmptyString.ValueHash256), Is.Zero);
            Assert.That(db.GetColumnDb(PbtColumns.FullLeaves).GetAll(), Is.Empty);
        }
    }

    [Test]
    public void Negative_window_size_does_not_publish_rebuild()
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Rebuild([], 1,
            new StateId(7, TestItem.KeccakA.ValueHash256), target, windowSize: -1));
        using IPbtPersistence.IReader reader = target.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(StateId.PreGenesis));
            Assert.That(reader.EnumerateNodeGroupKeys(), Is.Empty);
        }
    }

    [Test]
    public async Task Rebuild_commits_repeated_leaf_windows_before_source_completes([Values(1, 3)] int windowSize)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);
        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateBounded<ArrayPoolList<RebuildEntry>>(1);
        DrainedChunkReader source = new(channel.Reader);
        StateId targetState = new(7, TestItem.KeccakA.ValueHash256);
        Task<ValueHash256> rebuilding = new PbtRebuilder(target, LimboLogs.Instance)
            .Rebuild(source, targetState, CancellationToken.None, windowSize);
        using PbtNodeGroupStore expectedStore = new();
        ValueHash256 expectedRoot = default;
        try
        {
            for (int index = 0; index < 2; index++)
            {
                PbtStorageFullKey key = (PbtStorageFullKey)PbtStateKey.Code(TestItem.AddressA,
                    TestItem.KeccakB.ValueHash256, PbtKeyDerivation.HeaderCodeChunks + index);
                ArrayPoolList<RebuildEntry> chunk = new(windowSize);
                for (int repeat = 0; repeat < windowSize; repeat++) chunk.Add(new(key, TestItem.KeccakC.ValueHash256));
                await channel.Writer.WriteAsync(chunk);
                await source.Drained[index].Task.WaitAsync(TimeSpan.FromSeconds(10));

                using PbtWriteBatchBuilder<PbtStorageFullKey> changes = new(0);
                changes.Set(key, TestItem.KeccakC.ValueHash256);
                using PbtWriteBatch<PbtStorageFullKey> prepared = changes.Build();
                expectedRoot = TrieUpdater.UpdateRoot(expectedStore, expectedRoot, prepared);
                using IPbtPersistence.IReader reader = target.CreateReader();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(rebuilding.IsCompleted, Is.False);
                    Assert.That(reader.CurrentState, Is.EqualTo(StateId.PreGenesis));
                    Assert.That(target.IsValid, Is.False);
                    Assert.That(CanonicalGroups(reader.EnumerateNodeGroupKeys(), reader.GetNodeGroup),
                        Is.EqualTo(CanonicalGroups(expectedStore.EnumerateNodeGroupKeys(), expectedStore.GetNodeGroup)));
                    Assert.Throws<ObjectDisposedException>(() => chunk.AsSpan());
                }
            }
        }
        finally
        {
            channel.Writer.TryComplete();
            await rebuilding.WaitAsync(TimeSpan.FromSeconds(10));
        }
        using IPbtPersistence.IReader completedReader = target.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await rebuilding, Is.EqualTo(expectedRoot));
            Assert.That(completedReader.CurrentState, Is.EqualTo(targetState));
            Assert.That(target.IsValid, Is.True);
        }
    }

    [Test]
    public async Task Interrupted_source_does_not_publish_committed_windows([Values] bool cancel)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);
        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateBounded<ArrayPoolList<RebuildEntry>>(1);
        DrainedChunkReader source = new(channel.Reader);
        using CancellationTokenSource cancellation = new();
        Task<ValueHash256> rebuilding = new PbtRebuilder(target, LimboLogs.Instance)
            .Rebuild(source, new StateId(7, TestItem.KeccakA.ValueHash256), cancellation.Token, 1);
        ArrayPoolList<RebuildEntry> chunk = new(1) { new((PbtStorageFullKey)PbtStateKey.Account(TestItem.AddressA, 0), TestItem.KeccakB.ValueHash256) };
        try
        {
            await channel.Writer.WriteAsync(chunk);
            await source.Drained[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (cancel) await cancellation.CancelAsync();
            else channel.Writer.Complete(new InvalidDataException("source failed"));

            Exception? failure = null;
            try { await rebuilding.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception exception) { failure = exception; }
            using IPbtPersistence.IReader reader = target.CreateReader();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(failure, cancel ? Is.InstanceOf<OperationCanceledException>() : Is.TypeOf<InvalidDataException>());
                Assert.That(reader.CurrentState, Is.EqualTo(StateId.PreGenesis));
                Assert.That(reader.EnumerateNodeGroupKeys(), Is.Not.Empty);
                Assert.That(target.IsValid, Is.False);
                Assert.Throws<ObjectDisposedException>(() => chunk.AsSpan());
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            channel.Writer.TryComplete();
        }
    }

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

    private static string[] CanonicalGroups(IEnumerable<IPbtNodePath> groupKeys, Func<IPbtNodePath, RefCountingMemory?> getNodeGroup)
    {
        List<string> result = [];
        foreach (IPbtNodePath groupKey in groupKeys)
        {
            using RefCountingMemory? payload = getNodeGroup(groupKey);
            result.Add($"{Convert.ToHexString(groupKey.Encode())}:{Convert.ToHexString(payload!.GetSpan())}");
        }
        result.Sort(StringComparer.Ordinal);
        return [.. result];
    }

}
