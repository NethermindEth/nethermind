// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync.Snap;

[TestFixture]
public class FlatSnapTreesTests
{
    private static IPersistence.IPersistenceReader Reader() => Substitute.For<IPersistence.IPersistenceReader>();
    private static IPersistence.IWriteBatch WriteBatch() => Substitute.For<IPersistence.IWriteBatch>();

    [Test]
    public void StateTree_IsPersisted_TrueWhenStoredRlpHashMatches()
    {
        IPersistence.IPersistenceReader reader = Reader();
        IPersistence.IWriteBatch writer = WriteBatch();
        byte[] rlp = [0xc1, 0x42];
        ValueHash256 expectedHash = ValueKeccak.Compute(rlp);
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(rlp);

        FlatSnapStateTree tree = new(reader, writer, enableDoubleWriteCheck: false, LimboLogs.Instance);

        TreePath path = TreePath.FromHexString("12");
        tree.IsPersisted(path, expectedHash).Should().BeTrue();
    }

    [Test]
    public void StateTree_IsPersisted_FalseWhenRlpMissing()
    {
        IPersistence.IPersistenceReader reader = Reader();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns((byte[]?)null);

        FlatSnapStateTree tree = new(reader, WriteBatch(), enableDoubleWriteCheck: false, LimboLogs.Instance);

        tree.IsPersisted(TreePath.FromHexString("12"), default).Should().BeFalse();
    }

    [Test]
    public void StateTree_IsPersisted_FalseWhenHashMismatch()
    {
        IPersistence.IPersistenceReader reader = Reader();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns([0xc1, 0x42]);

        FlatSnapStateTree tree = new(reader, WriteBatch(), enableDoubleWriteCheck: false, LimboLogs.Instance);

        ValueHash256 wrongHash = new(Bytes.FromHexString("11" + new string('0', 62)));
        tree.IsPersisted(TreePath.FromHexString("12"), wrongHash).Should().BeFalse();
    }

    [Test]
    public void StateTree_Dispose_DisposesReaderAndWriteBatch()
    {
        IPersistence.IPersistenceReader reader = Reader();
        IPersistence.IWriteBatch writer = WriteBatch();
        FlatSnapStateTree tree = new(reader, writer, enableDoubleWriteCheck: false, LimboLogs.Instance);

        tree.Dispose();

        reader.Received(1).Dispose();
        writer.Received(1).Dispose();
    }

    [Test]
    public void StorageTree_IsPersisted_DelegatesToStorageRlpLookup()
    {
        IPersistence.IPersistenceReader reader = Reader();
        byte[] rlp = [0xc1, 0x99];
        Hash256 addressHash = Keccak.Compute(Bytes.FromHexString("aa"));
        reader.TryLoadStorageRlp(addressHash, Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(rlp);

        FlatSnapStorageTree tree = new(reader, WriteBatch(), addressHash, enableDoubleWriteCheck: false, LimboLogs.Instance);

        ValueHash256 expectedHash = ValueKeccak.Compute(rlp);
        tree.IsPersisted(TreePath.FromHexString("ab"), expectedHash).Should().BeTrue();
        tree.IsPersisted(TreePath.FromHexString("ab"), default).Should().BeFalse();
    }

    [Test]
    public void StorageTree_Dispose_DisposesReaderAndWriteBatch()
    {
        IPersistence.IPersistenceReader reader = Reader();
        IPersistence.IWriteBatch writer = WriteBatch();
        FlatSnapStorageTree tree = new(reader, writer, Keccak.Zero, enableDoubleWriteCheck: false, LimboLogs.Instance);

        tree.Dispose();

        reader.Received(1).Dispose();
        writer.Received(1).Dispose();
    }

    [Test]
    public void StateTree_BulkSetThenCommit_FiltersByUpperBound()
    {
        IPersistence.IPersistenceReader reader = Reader();
        reader.GetAccountRaw(Arg.Any<ValueHash256>()).Returns((byte[]?)null);
        IPersistence.IWriteBatch writer = WriteBatch();

        FlatSnapStateTree tree = new(reader, writer, enableDoubleWriteCheck: false, LimboLogs.Instance);

        Account account = new(1, 100);
        ValueHash256 lowPath = new(Bytes.FromHexString("11" + new string('0', 62)));
        ValueHash256 highPath = new(Bytes.FromHexString("99" + new string('0', 62)));
        ValueHash256 upperBound = new(Bytes.FromHexString("55" + new string('0', 62)));

        List<PathWithAccount> entries = [new PathWithAccount(lowPath, account), new PathWithAccount(highPath, account)];
        tree.BulkSetAndUpdateRootHash(entries);

        tree.Commit(upperBound);

        // Only the in-bound entry should be written.
        writer.Received(1).SetAccountRaw(lowPath, account);
        writer.DidNotReceive().SetAccountRaw(highPath, Arg.Any<Account>());
    }

    [Test]
    public void StateTree_DoubleWriteCheck_ThrowsWhenAccountAlreadyPresent()
    {
        IPersistence.IPersistenceReader reader = Reader();
        IPersistence.IWriteBatch writer = WriteBatch();
        // Simulate the slot already being persisted.
        reader.GetAccountRaw(Arg.Any<ValueHash256>()).Returns([0x01]);

        FlatSnapStateTree tree = new(reader, writer, enableDoubleWriteCheck: true, LimboLogs.Instance);

        ValueHash256 path = new(Bytes.FromHexString("11" + new string('0', 62)));
        List<PathWithAccount> entries = [new PathWithAccount(path, new Account(1, 100))];
        tree.BulkSetAndUpdateRootHash(entries);

        Assert.That(() => tree.Commit(new ValueHash256(Bytes.FromHexString("ff" + new string('f', 62)))),
            Throws.Exception.With.Message.Contain("Double account flat write"));
    }
}
