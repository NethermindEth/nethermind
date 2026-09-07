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

    private static List<RebuildEntry> BuildFixture(Dictionary<string, byte[]> model)
    {
        // > 128 chunks (3968 bytes) so overflow chunks land in the content-addressed code zone
        byte[] bigCode = new byte[5000];
        for (int i = 0; i < bigCode.Length; i += 10) bigCode[i] = 0x63; // PUSH4, to exercise the chunk PUSHDATA offsets
        byte[] smallCode = Bytes.FromHexString("0x60016002");

        List<RebuildEntry> leaves = [];

        void AddAccount(Address address, ulong nonce, in UInt256 balance, byte[]? code)
        {
            PbtReferenceModel.SetAccount(model, address, nonce, balance, code);
            Account account = code is { Length: > 0 }
                ? new Account(nonce, balance).WithChangedCodeHash(Keccak.Compute(code))
                : new Account(nonce, balance);
            PbtTestLeaves.AddAccount(leaves, address, account, code is { Length: > 0 } ? code : null);
        }

        void AddSlot(Address address, in UInt256 slot, in UInt256 value)
        {
            PbtReferenceModel.SetSlot(model, address, slot, value);
            PbtTestLeaves.AddSlot(leaves, address, slot, value);
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

        return leaves;
    }

    private static async Task<ValueHash256> Rebuild(
        List<RebuildEntry> leaves,
        int chunkSize,
        StateId targetState,
        PbtRocksDbPersistence target,
        ILogManager? logManager = null)
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

        return await rebuilder.Rebuild(channel.Reader, targetState, CancellationToken.None);
    }

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(int.MaxValue)]
    public async Task Rebuild_matches_reference_root_across_channel_chunks(int chunkSize)
    {
        Dictionary<string, byte[]> model = [];
        List<RebuildEntry> leaves = BuildFixture(model);

        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);

        // the header root the source claims is unrelated to the tree root the fold produces, so the
        // two must be recorded separately rather than one standing in for the other
        StateId targetState = new(7, TestItem.KeccakA.ValueHash256);
        ValueHash256 root = await Rebuild(leaves, chunkSize, targetState, target);

        using PbtNodeGroupStore incrementalStore = new();
        ValueHash256 incrementalRoot = default;
        using IPbtPersistence.IReader reader = target.CreateReader();
        foreach ((PbtFullKey key, ValueHash256 value) in PbtFlatState.EnumerateLeaves(reader))
        {
            PbtWriteBatch incrementalChange = new();
            incrementalChange.Set(key, value);
            incrementalRoot = TrieUpdater.UpdateRoot(incrementalStore, incrementalRoot, incrementalChange);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)), "rebuilt root must match the EIP reference tree");
            Assert.That(root, Is.EqualTo(incrementalRoot), "incremental replay and one-batch rebuild must have the same root");
            Assert.That(CanonicalGroups(reader.EnumerateNodeGroupKeys(), reader.GetNodeGroup),
                Is.EqualTo(CanonicalGroups(incrementalStore.EnumerateNodeGroupKeys(), incrementalStore.GetNodeGroup)),
                "incremental replay and one-batch rebuild must have the exact same group keys and payloads");
            Assert.That(reader.CurrentState, Is.EqualTo(targetState), "persisted state pointer must advance to the rebuilt state");
            Assert.That(reader.CurrentRoot, Is.EqualTo(root), "and record the tree root beside it");
        }
    }

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(int.MaxValue)]
    public async Task Rebuild_is_independent_of_entry_and_chunk_order(int chunkSize)
    {
        Dictionary<string, byte[]> model = [];
        List<RebuildEntry> leaves = BuildFixture(model);

        Random random = new(42);
        for (int i = leaves.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (leaves[i], leaves[j]) = (leaves[j], leaves[i]);
        }

        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);

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

        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);
        await Rebuild(leaves, int.MaxValue, new StateId(7, TestItem.KeccakA.ValueHash256), target, logManager);

        Assert.That(messages, Has.Some.Contains("PBT rebuild complete").And.Some.Contains("3 leaves"));
    }

    [Test]
    public async Task Rebuild_empty_source_produces_empty_tree()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
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

    [TestCase(false)]
    [TestCase(true)]
    public async Task Rebuild_uses_final_logical_values_and_code_references(bool deleteAccount)
    {
        byte[] originalCode = Bytes.FromHexString("0x60016002");
        byte[] replacementCode = Bytes.FromHexString("0x6003");
        Account original = new Account(1, 10).WithChangedCodeHash(Keccak.Compute(originalCode));
        Account replacement = new Account(0, 0).WithChangedCodeHash(Keccak.Compute(replacementCode));
        List<RebuildEntry> entries = [];
        PbtTestLeaves.AddAccount(entries, TestItem.AddressA, original, originalCode);
        PbtTestLeaves.AddSlot(entries, TestItem.AddressA, 63, 1);
        PbtTestLeaves.AddSlot(entries, TestItem.AddressA, 1000, 2);
        PbtTestLeaves.AddAccount(entries, TestItem.AddressA, replacement, replacementCode);
        PbtTestLeaves.AddSlot(entries, TestItem.AddressA, 63, 0);
        PbtTestLeaves.AddSlot(entries, TestItem.AddressA, 1000, 0);
        if (deleteAccount) entries.Add(RebuildEntry.FromAccount(PbtKeyDerivation.AddressKeyHash(TestItem.AddressA), null));

        Dictionary<string, byte[]> model = [];
        if (!deleteAccount) PbtReferenceModel.SetAccount(model, TestItem.AddressA, 0, 0, replacementCode);
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);
        ValueHash256 root = await Rebuild(entries, 1, new StateId(7, TestItem.KeccakA.ValueHash256), target);
        using IPbtPersistence.IReader reader = target.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressA), Is.EqualTo(deleteAccount ? null : replacement));
            Assert.That(reader.GetCodeReference(original.CodeHash.ValueHash256), Is.Zero);
            Assert.That(reader.GetCodeReference(replacement.CodeHash.ValueHash256), Is.EqualTo(deleteAccount ? 0 : 1));
            Assert.That(reader.EnumerateStorage(), Is.Empty);
            Assert.That(reader.GetCode(original.CodeHash.ValueHash256)!.Code.ToArray(), Is.EqualTo(originalCode));
            Assert.That(db.GetColumnDb(PbtColumns.FullLeaves).GetAll(), Is.Empty);
        }
    }

    [Test]
    public void Missing_code_does_not_publish_partial_rebuild()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);
        List<RebuildEntry> entries = [RebuildEntry.FromAccount(PbtKeyDerivation.AddressKeyHash(TestItem.AddressA),
            new Account(1, 2).WithChangedCodeHash(TestItem.KeccakA))];
        Assert.ThrowsAsync<InvalidDataException>(() => Rebuild(entries, 1, new StateId(7, TestItem.KeccakA.ValueHash256), target));
        using IPbtPersistence.IReader reader = target.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(StateId.PreGenesis));
            Assert.That(reader.EnumerateAccounts(), Is.Empty);
            Assert.That(reader.EnumerateNodeGroupKeys(), Is.Empty);
        }
    }

    private static string[] CanonicalGroups(IEnumerable<PbtNodePath> groupKeys, Func<PbtNodePath, RefCountingMemory?> getNodeGroup)
    {
        List<string> result = [];
        foreach (PbtNodePath groupKey in groupKeys)
        {
            using RefCountingMemory? payload = getNodeGroup(groupKey);
            result.Add($"{Convert.ToHexString(groupKey.Encode())}:{Convert.ToHexString(payload!.GetSpan())}");
        }
        result.Sort(StringComparer.Ordinal);
        return [.. result];
    }

}
