// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
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
        PbtTestLeaves.SortByTreeKey(leaves);

        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);

        // the header root the source claims is unrelated to the tree root the fold produces, so the
        // two must be recorded separately rather than one standing in for the other
        StateId targetState = new(7, TestItem.KeccakA.ValueHash256);
        ValueHash256 root = await Rebuild(leaves, chunkSize, targetState, target);

        using PbtNodeGroupStore incrementalStore = new();
        ValueHash256 incrementalRoot = default;
        foreach (RebuildEntry leaf in leaves)
        {
            PbtWriteBatch incrementalChange = new();
            incrementalChange.Set(leaf.Key, leaf.Leaf);
            incrementalRoot = TrieUpdater.UpdateRoot(incrementalStore, incrementalRoot, incrementalChange);
        }

        using IPbtPersistence.IReader reader = target.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)), "rebuilt root must match the EIP reference tree");
            Assert.That(root, Is.EqualTo(incrementalRoot), "incremental replay and one-batch rebuild must have the same root");
            Assert.That(CanonicalNodes(reader.EnumerateNodes()), Is.EqualTo(CanonicalNodes(incrementalStore.EnumerateRecords())),
                "incremental replay and one-batch rebuild must have the exact same path/node graph");
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

        List<RebuildEntry> leaves =
        [
            Entry(0x04, 0x00), // Account: 25% after its 0000 fixed prefix.
            Entry(0x18, 0x00), // Code: 50% after its 0001 fixed prefix.
            Entry(0xE0, 0x00), // Storage: 75% after its 1 fixed prefix.
        ];
        PbtTestLeaves.SortByTreeKey(leaves);

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

    private static string[] CanonicalNodes(IEnumerable<KeyValuePair<PbtNodePath, byte[]>> nodes)
    {
        List<string> result = [];
        foreach ((PbtNodePath path, byte[] encoding) in nodes)
            result.Add($"{Convert.ToHexString(path.Encode())}:{Convert.ToHexString(encoding)}");
        result.Sort(StringComparer.Ordinal);
        return [.. result];
    }

    private static string[] CanonicalNodes(IReadOnlyList<PbtNodeRecord> nodes)
    {
        List<string> result = new(nodes.Count);
        foreach (PbtNodeRecord node in nodes)
            result.Add($"{Convert.ToHexString(node.Path.Encode())}:{Convert.ToHexString(node.Encoding.Span)}");
        result.Sort(StringComparer.Ordinal);
        return [.. result];
    }

    private static RebuildEntry Entry(byte first, byte second)
    {
        byte[] key = new byte[Eip8297KeyDerivation.AccountKeyLength];
        key[0] = first;
        key[1] = second;
        return new RebuildEntry(new PbtFullKey(key), TestItem.KeccakA.ValueHash256);
    }
}
