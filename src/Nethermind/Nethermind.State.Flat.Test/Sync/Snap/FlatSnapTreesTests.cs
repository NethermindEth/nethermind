// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync.Snap;

[TestFixture]
public class FlatSnapTreesTests
{
    private static SubstitutablePersistenceReader Reader() => Substitute.For<SubstitutablePersistenceReader>();
    private static IPersistence.IAtomicPersistenceBatch WriteBatch() => Substitute.For<IPersistence.IAtomicPersistenceBatch>();

    private static FlatSnapStateTree NewStateTree(IPersistence.IPersistenceReader reader, IPersistence.IAtomicPersistenceBatch? writer = null, bool enableDoubleWriteCheck = false) =>
        new(reader, writer ?? WriteBatch(), enableDoubleWriteCheck, LimboLogs.Instance);

    private static FlatSnapStorageTree NewStorageTree(IPersistence.IPersistenceReader reader, Hash256 addressHash, IPersistence.IAtomicPersistenceBatch? writer = null) =>
        new(reader, writer ?? WriteBatch(), addressHash, enableDoubleWriteCheck: false, LimboLogs.Instance);

    private static ValueHash256 PathHash(string prefix) => new(Bytes.FromHexString(prefix + new string('0', 64 - prefix.Length)));

    private static IEnumerable<TestCaseData> StateIsPersistedCases()
    {
        byte[] rlp = [0xc1, 0x42];
        yield return new TestCaseData(rlp, (ValueHash256?)ValueKeccak.Compute(rlp), true).SetName("match");
        yield return new TestCaseData((byte[]?)null, (ValueHash256?)null, false).SetName("missing rlp");
        yield return new TestCaseData(rlp, (ValueHash256?)new ValueHash256(Bytes.FromHexString("11" + new string('0', 62))), false).SetName("hash mismatch");
    }

    [TestCaseSource(nameof(StateIsPersistedCases))]
    public void StateTree_IsPersisted(byte[]? storedRlp, ValueHash256? expectedHash, bool expected)
    {
        SubstitutablePersistenceReader reader = Reader();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(storedRlp);

        using FlatSnapStateTree tree = NewStateTree(reader);

        Assert.That(tree.IsPersisted(TreePath.FromHexString("12"), expectedHash ?? default), Is.EqualTo(expected));
    }

    [Test]
    public void StorageTree_IsPersisted_DelegatesToStorageRlpLookup()
    {
        SubstitutablePersistenceReader reader = Reader();
        byte[] rlp = [0xc1, 0x99];
        Hash256 addressHash = Keccak.Compute(Bytes.FromHexString("aa"));
        reader.TryLoadStorageRlp(addressHash, Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(rlp);

        using FlatSnapStorageTree tree = NewStorageTree(reader, addressHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.IsPersisted(TreePath.FromHexString("ab"), ValueKeccak.Compute(rlp)), Is.True);
            Assert.That(tree.IsPersisted(TreePath.FromHexString("ab"), default), Is.False);
        }
    }

    private static IEnumerable<TestCaseData> DisposeCases()
    {
        yield return new TestCaseData((Func<IPersistence.IPersistenceReader, IPersistence.IAtomicPersistenceBatch, IDisposable>)
            ((r, w) => new FlatSnapStateTree(r, w, enableDoubleWriteCheck: false, LimboLogs.Instance))).SetName("state tree");
        yield return new TestCaseData((Func<IPersistence.IPersistenceReader, IPersistence.IAtomicPersistenceBatch, IDisposable>)
            ((r, w) => new FlatSnapStorageTree(r, w, Keccak.Zero, enableDoubleWriteCheck: false, LimboLogs.Instance))).SetName("storage tree");
    }

    [TestCaseSource(nameof(DisposeCases))]
    public void Dispose_DisposesReaderAndWriteBatch(Func<IPersistence.IPersistenceReader, IPersistence.IAtomicPersistenceBatch, IDisposable> build)
    {
        IPersistence.IPersistenceReader reader = Reader();
        IPersistence.IAtomicPersistenceBatch writer = WriteBatch();

        build(reader, writer).Dispose();

        reader.Received(1).Dispose();
        writer.Received(1).Dispose();
    }

    [Test]
    public void StorageTree_DeleteOutsideChildren_deletes_slots_and_nodes_under_each_empty_run()
    {
        Hash256 addressHash = Keccak.Compute("storage-delete"u8);
        IPersistence.IAtomicPersistenceBatch writer = WriteBatch();
        using FlatSnapStorageTree tree = NewStorageTree(Reader(), addressHash, writer);
        TrieNode branch = TrieNodeFactory.CreateBranch();
        branch.SetChild(0, new TrieNode(NodeType.Unknown, Keccak.Compute("a"u8)));
        branch.SetChild(5, new TrieNode(NodeType.Unknown, Keccak.Compute("b"u8)));

        tree.DeleteOutsideChildren(TreePath.Empty, branch, ValueKeccak.Zero, ValueKeccak.MaxValue);

        (ValueHash256 From, ValueHash256 To)[] expected =
        [
            (PathHash("1"), PathHash("4", 'f')),
            (PathHash("6"), ValueKeccak.MaxValue),
        ];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.DeleteStorageRange), addressHash), Is.EqualTo(expected));
            Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.DeleteStorageTrieNodeRange), addressHash), Is.EqualTo(expected));
        }
    }

    [Test]
    public void StorageTree_DeleteOutsideChildren_deletes_slots_and_nodes_beside_an_extension_key()
    {
        Hash256 addressHash = Keccak.Compute("storage-delete"u8);
        IPersistence.IAtomicPersistenceBatch writer = WriteBatch();
        using FlatSnapStorageTree tree = NewStorageTree(Reader(), addressHash, writer);
        TrieNode extension = TrieNodeFactory.CreateExtension([1, 2], new TrieNode(NodeType.Unknown, Keccak.Compute("a"u8)));

        tree.DeleteOutsideChildren(TreePath.Empty, extension, ValueKeccak.Zero, ValueKeccak.MaxValue);
        writer.Received(1).DeleteStorageTrieNode(addressHash, TreePath.FromHexString("1"));
        writer.DidNotReceive().DeleteStorageTrieNode(addressHash, TreePath.FromHexString("12"));

        (ValueHash256 From, ValueHash256 To)[] expected =
        [
            (ValueKeccak.Zero, new ValueHash256(Bytes.FromHexString("11" + new string('f', 62)))),
            (PathHash("13"), ValueKeccak.MaxValue),
        ];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.DeleteStorageRange), addressHash), Is.EqualTo(expected));
            Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.DeleteStorageTrieNodeRange), addressHash), Is.EqualTo(expected));
        }
    }

    // The batch probes one snapshot for its whole life, so cleanup bounds found empty stay empty for every boundary node.
    [Test]
    public void StorageTree_DeleteOutsideChildren_probes_empty_cleanup_bounds_once_and_deletes_nothing()
    {
        Hash256 addressHash = Keccak.Compute("storage-delete"u8);
        IPersistence.IAtomicPersistenceBatch writer = WriteBatch();
        writer.IsStorageRangeEmpty(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256>(), Arg.Any<ValueHash256>()).Returns(true);
        using FlatSnapStorageTree tree = NewStorageTree(Reader(), addressHash, writer);
        TrieNode branch = TrieNodeFactory.CreateBranch();
        branch.SetChild(0, new TrieNode(NodeType.Unknown, Keccak.Compute("a"u8)));
        branch.SetChild(5, new TrieNode(NodeType.Unknown, Keccak.Compute("b"u8)));
        TrieNode extension = TrieNodeFactory.CreateExtension([1, 2], new TrieNode(NodeType.Unknown, Keccak.Compute("c"u8)));

        tree.DeleteOutsideChildren(TreePath.Empty, branch, ValueKeccak.Zero, ValueKeccak.MaxValue);
        tree.DeleteOutsideChildren(TreePath.FromHexString("5"), extension, ValueKeccak.Zero, ValueKeccak.MaxValue);

        using (Assert.EnterMultipleScope())
        {
            writer.Received(1).IsStorageRangeEmpty(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256>(), Arg.Any<ValueHash256>());
            writer.Received(1).IsStorageRangeEmpty(addressHash.ValueHash256, ValueKeccak.Zero, ValueKeccak.MaxValue);
            writer.DidNotReceiveWithAnyArgs().DeleteStorageRange(default, default, default);
            writer.DidNotReceiveWithAnyArgs().DeleteStorageTrieNodeRange(default, default, default);
            writer.DidNotReceiveWithAnyArgs().DeleteStorageTrieNode(default, default);
        }
    }

    private static ValueHash256 PathHash(string prefix, char fill) => new(Bytes.FromHexString(prefix + new string(fill, 64 - prefix.Length)));

    private static TrieNode BranchWithLeafAtZeroAndHashAtFive()
    {
        byte[] leafKey = new byte[63];
        leafKey[0] = 1;
        TrieNode branch = TrieNodeFactory.CreateBranch();
        branch.SetChild(0, TrieNodeFactory.CreateLeaf(leafKey, new byte[] { 0x01 }));
        branch.SetChild(5, new TrieNode(NodeType.Unknown, Keccak.Compute("b"u8)));
        return branch;
    }

    private static readonly (ValueHash256 From, ValueHash256 To)[] BesideLeafAtZeroAndEmptyRuns =
    [
        (ValueKeccak.Zero, PathHash("00", 'f')),
        (new ValueHash256(Bytes.FromHexString("01" + new string('0', 61) + "1")), PathHash("0", 'f')),
        (PathHash("1"), PathHash("4", 'f')),
        (PathHash("6"), ValueKeccak.MaxValue),
    ];

    private static readonly (ValueHash256 From, ValueHash256 To)[] WholeAccountKeysBesideLeafAtZeroAndEmptyRuns =
    [
        (ValueKeccak.Zero, PathHash("00", 'f')),
        (new ValueHash256(Bytes.FromHexString("01" + new string('0', 36) + "01" + new string('0', 24))), PathHash("0", 'f')),
        (PathHash("1"), PathHash("4", 'f')),
        (PathHash("6"), ValueKeccak.MaxValue),
    ];

    [Test]
    public void StorageTree_DeleteOutsideChildren_deletes_beside_the_key_of_a_leaf_child()
    {
        Hash256 addressHash = Keccak.Compute("storage-delete"u8);
        IPersistence.IAtomicPersistenceBatch writer = WriteBatch();
        using FlatSnapStorageTree tree = NewStorageTree(Reader(), addressHash, writer);

        tree.DeleteOutsideChildren(TreePath.Empty, BranchWithLeafAtZeroAndHashAtFive(), ValueKeccak.Zero, ValueKeccak.MaxValue);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.DeleteStorageRange), addressHash), Is.EqualTo(BesideLeafAtZeroAndEmptyRuns));
            Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.DeleteStorageTrieNodeRange), addressHash), Is.EqualTo(BesideLeafAtZeroAndEmptyRuns));
        }
    }

    [Test]
    public void StateTree_DeleteOutsideChildren_deletes_accounts_and_nodes_under_empty_runs_and_beside_a_leaf_child()
    {
        IPersistence.IAtomicPersistenceBatch writer = WriteBatch();
        using FlatSnapStateTree tree = NewStateTree(Reader(), writer);

        tree.DeleteOutsideChildren(TreePath.Empty, BranchWithLeafAtZeroAndHashAtFive(), ValueKeccak.Zero, ValueKeccak.MaxValue);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.DeleteAccountRange)), Is.EqualTo(WholeAccountKeysBesideLeafAtZeroAndEmptyRuns));
            Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.DeleteStateTrieNodeRange)), Is.EqualTo(BesideLeafAtZeroAndEmptyRuns));
        }
    }

    [Test]
    public void StateTree_DeleteOutsideChildren_deletes_accounts_and_nodes_beside_an_extension_key()
    {
        IPersistence.IAtomicPersistenceBatch writer = WriteBatch();
        using FlatSnapStateTree tree = NewStateTree(Reader(), writer);
        TrieNode extension = TrieNodeFactory.CreateExtension([1, 2], new TrieNode(NodeType.Unknown, Keccak.Compute("a"u8)));

        tree.DeleteOutsideChildren(TreePath.Empty, extension, ValueKeccak.Zero, ValueKeccak.MaxValue);

        (ValueHash256 From, ValueHash256 To)[] expected =
        [
            (ValueKeccak.Zero, PathHash("11", 'f')),
            (PathHash("13"), ValueKeccak.MaxValue),
        ];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.DeleteAccountRange)), Is.EqualTo(expected));
            Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.DeleteStateTrieNodeRange)), Is.EqualTo(expected));
        }
    }

    private static byte[] StoredLeafRlp(byte firstKeyNibble)
    {
        byte[] leafKey = new byte[63];
        leafKey[0] = firstKeyNibble;
        TrieNode leaf = TrieNodeFactory.CreateLeaf(leafKey, new byte[] { 0x01 });
        TreePath path = TreePath.Empty;
        leaf.ResolveKey(NullTrieNodeResolver.Instance, ref path);
        return leaf.FullRlp.ToArray()!;
    }

    [Test]
    public void StorageTree_DeleteOutsideChildren_reads_a_hash_only_child_from_the_store([Values] bool storedNodeMatches)
    {
        Hash256 addressHash = Keccak.Compute("storage-delete"u8);
        byte[] leafRlp = StoredLeafRlp(2);
        SubstitutablePersistenceReader reader = Reader();
        reader.TryLoadStorageRlp(addressHash, TreePath.FromHexString("5"), Arg.Any<ReadFlags>())
            .Returns(storedNodeMatches ? leafRlp : StoredLeafRlp(3));
        IPersistence.IAtomicPersistenceBatch writer = WriteBatch();
        using FlatSnapStorageTree tree = NewStorageTree(reader, addressHash, writer);
        TrieNode branch = TrieNodeFactory.CreateBranch();
        branch.SetChild(5, new TrieNode(NodeType.Unknown, Keccak.Compute(leafRlp)));

        tree.DeleteOutsideChildren(TreePath.Empty, branch, ValueKeccak.Zero, ValueKeccak.MaxValue);

        List<(ValueHash256 From, ValueHash256 To)> expected = [(ValueKeccak.Zero, PathHash("4", 'f'))];
        if (storedNodeMatches)
        {
            expected.Add((PathHash("5"), PathHash("51", 'f')));
            expected.Add((new ValueHash256(Bytes.FromHexString("52" + new string('0', 61) + "1")), PathHash("5", 'f')));
        }

        expected.Add((PathHash("6"), ValueKeccak.MaxValue));
        Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.DeleteStorageRange), addressHash), Is.EqualTo(expected));
    }

    [Test]
    public void StateTree_DeleteOutsideChildren_deletes_the_storage_of_removed_accounts_but_not_of_the_kept_leaf()
    {
        IPersistence.IAtomicPersistenceBatch writer = WriteBatch();
        using FlatSnapStateTree tree = NewStateTree(Reader(), writer);

        tree.DeleteOutsideChildren(TreePath.Empty, BranchWithLeafAtZeroAndHashAtFive(), ValueKeccak.Zero, ValueKeccak.MaxValue);

        Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.SelfDestructRange)), Is.EqualTo(WholeAccountKeysBesideLeafAtZeroAndEmptyRuns));
    }

    [Test]
    public void StateTree_DeleteOutsideChildren_limits_deletion_to_the_partition()
    {
        IPersistence.IAtomicPersistenceBatch writer = WriteBatch();
        using FlatSnapStateTree tree = NewStateTree(Reader(), writer);
        ValueHash256 partitionStart = PathHash("2");
        ValueHash256 partitionEnd = PathHash("7", 'f');

        tree.DeleteOutsideChildren(TreePath.Empty, BranchWithLeafAtZeroAndHashAtFive(), partitionStart, partitionEnd);

        (ValueHash256 From, ValueHash256 To)[] expected = [(partitionStart, PathHash("4", 'f')), (PathHash("6"), partitionEnd)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.DeleteAccountRange)), Is.EqualTo(expected));
            Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.DeleteStateTrieNodeRange)), Is.EqualTo(expected));
            Assert.That(DeletedRanges(writer, nameof(IPersistence.IWriteBatch.SelfDestructRange)), Is.EqualTo(expected));
        }
    }

    private static ValueHash256 AccountPath(string key, string tail) => new(Bytes.FromHexString(key + tail));

    [Test]
    public void StateTree_storage_range_covers_only_whole_account_keys()
    {
        string key = "01" + new string('0', 38);
        string nextKey = "01" + new string('0', 36) + "01";
        bool insideOneKey = FlatSnapStateTree.TryGetWholeAccountKeys(
            AccountPath(key, new string('0', 23) + "1"), AccountPath(key, new string('f', 23) + "e"), out _, out _);
        bool acrossKeys = FlatSnapStateTree.TryGetWholeAccountKeys(
            AccountPath(key, new string('0', 23) + "1"), AccountPath(nextKey, new string('f', 24)), out ValueHash256 first, out ValueHash256 last);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(insideOneKey, Is.False);
            Assert.That(acrossKeys, Is.True);
            Assert.That(first, Is.EqualTo(AccountPath(nextKey, new string('0', 24))));
            Assert.That(last, Is.EqualTo(AccountPath(nextKey, new string('f', 24))));
        }
    }

    /// <summary>The ranges of the <paramref name="method"/> calls, of the storage of <paramref name="addressHash"/> or, when null, of the state.</summary>
    private static List<(ValueHash256 From, ValueHash256 To)> DeletedRanges(IPersistence.IWriteBatch writer, string method, Hash256? addressHash = null)
    {
        int first = addressHash is null ? 0 : 1;
        List<(ValueHash256, ValueHash256)> ranges = [];
        foreach (ICall call in writer.ReceivedCalls())
        {
            object?[] arguments = call.GetArguments();
            if (call.GetMethodInfo().Name == method && (addressHash is null || (ValueHash256)arguments[0]! == addressHash.ValueHash256))
            {
                ranges.Add(((ValueHash256)arguments[first]!, (ValueHash256)arguments[first + 1]!));
            }
        }

        return ranges;
    }

    [Test]
    public void StateTree_BulkSetThenCommit_FiltersByUpperBound()
    {
        IPersistence.IPersistenceReader reader = Reader();
        reader.GetAccountRaw(Arg.Any<ValueHash256>()).Returns((byte[]?)null);
        FakeWriteBatch writer = new();
        using FlatSnapStateTree tree = NewStateTree(reader, writer);

        AccountStruct account = new(1, 100);
        ValueHash256 lowPath = PathHash("11");
        ValueHash256 highPath = PathHash("99");

        tree.BulkSetAndUpdateRootHash([new PathWithAccount(lowPath, account), new PathWithAccount(highPath, account)]);
        tree.Commit(PathHash("55"));

        Assert.That(writer.SetAccountRawCalls, Has.One.EqualTo((lowPath, account)));
    }

    [Test]
    public void StateTree_DoubleWriteCheck_ThrowsWhenAccountAlreadyPresent()
    {
        IPersistence.IPersistenceReader reader = Reader();
        reader.GetAccountRaw(Arg.Any<ValueHash256>()).Returns([0x01]);
        using FlatSnapStateTree tree = NewStateTree(reader, enableDoubleWriteCheck: true);

        tree.BulkSetAndUpdateRootHash([new PathWithAccount(PathHash("11"), new AccountStruct(1, 100))]);

        Assert.That(() => tree.Commit(new ValueHash256(Bytes.FromHexString("ff" + new string('f', 62)))),
            Throws.Exception.With.Message.Contain("Double account flat write"));
    }
}
