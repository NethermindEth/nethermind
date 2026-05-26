// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
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

    private static FlatSnapStateTree NewStateTree(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch? writer = null, bool enableDoubleWriteCheck = false) =>
        new(reader, writer ?? WriteBatch(), enableDoubleWriteCheck, LimboLogs.Instance);

    private static FlatSnapStorageTree NewStorageTree(IPersistence.IPersistenceReader reader, Hash256 addressHash, IPersistence.IWriteBatch? writer = null) =>
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
        IPersistence.IPersistenceReader reader = Reader();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(storedRlp);

        using FlatSnapStateTree tree = NewStateTree(reader);

        tree.IsPersisted(TreePath.FromHexString("12"), expectedHash ?? default).Should().Be(expected);
    }

    [Test]
    public void StorageTree_IsPersisted_DelegatesToStorageRlpLookup()
    {
        IPersistence.IPersistenceReader reader = Reader();
        byte[] rlp = [0xc1, 0x99];
        Hash256 addressHash = Keccak.Compute(Bytes.FromHexString("aa"));
        reader.TryLoadStorageRlp(addressHash, Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(rlp);

        using FlatSnapStorageTree tree = NewStorageTree(reader, addressHash);

        tree.IsPersisted(TreePath.FromHexString("ab"), ValueKeccak.Compute(rlp)).Should().BeTrue();
        tree.IsPersisted(TreePath.FromHexString("ab"), default).Should().BeFalse();
    }

    private static IEnumerable<TestCaseData> DisposeCases()
    {
        yield return new TestCaseData((Func<IPersistence.IPersistenceReader, IPersistence.IWriteBatch, IDisposable>)
            ((r, w) => new FlatSnapStateTree(r, w, enableDoubleWriteCheck: false, LimboLogs.Instance))).SetName("state tree");
        yield return new TestCaseData((Func<IPersistence.IPersistenceReader, IPersistence.IWriteBatch, IDisposable>)
            ((r, w) => new FlatSnapStorageTree(r, w, Keccak.Zero, enableDoubleWriteCheck: false, LimboLogs.Instance))).SetName("storage tree");
    }

    [TestCaseSource(nameof(DisposeCases))]
    public void Dispose_DisposesReaderAndWriteBatch(Func<IPersistence.IPersistenceReader, IPersistence.IWriteBatch, IDisposable> build)
    {
        IPersistence.IPersistenceReader reader = Reader();
        IPersistence.IWriteBatch writer = WriteBatch();

        build(reader, writer).Dispose();

        reader.Received(1).Dispose();
        writer.Received(1).Dispose();
    }

    [Test]
    public void StateTree_BulkSetThenCommit_FiltersByUpperBound()
    {
        IPersistence.IPersistenceReader reader = Reader();
        reader.GetAccountRaw(Arg.Any<ValueHash256>()).Returns((byte[]?)null);
        // Manual stub: NSubstitute/Castle DynamicProxy cannot generate valid IL for
        // IWriteBatch.SetStateTrieNode (the combination of `in TreePath` with
        // `ReadOnlySpan<byte>` triggers InvalidProgramException at proxy invocation
        // time). The commit path calls SetStateTrieNode for the in-bound key, so this
        // test cannot use Substitute.For<IPersistence.IWriteBatch>().
        RecordingWriteBatch writer = new();
        using FlatSnapStateTree tree = NewStateTree(reader, writer);

        Account account = new(1, 100);
        ValueHash256 lowPath = PathHash("11");
        ValueHash256 highPath = PathHash("99");

        tree.BulkSetAndUpdateRootHash([new PathWithAccount(lowPath, account), new PathWithAccount(highPath, account)]);
        tree.Commit(PathHash("55"));

        writer.SetAccountRawCalls.Should().ContainSingle();
        writer.SetAccountRawCalls[0].Path.Should().Be(lowPath);
        writer.SetAccountRawCalls[0].Account.Should().Be(account);
    }

    /// <summary>
    /// Manual <see cref="IPersistence.IWriteBatch"/> stub used by tests whose commit
    /// path invokes <c>SetStateTrieNode(in TreePath, ReadOnlySpan&lt;byte&gt;)</c> —
    /// NSubstitute's Castle DynamicProxy cannot generate valid IL for that
    /// signature (in/ref + ref struct combination), so a substitute would throw
    /// <see cref="System.InvalidProgramException"/> on first invocation.
    /// </summary>
    private sealed class RecordingWriteBatch : IPersistence.IWriteBatch
    {
        public List<(ValueHash256 Path, Account Account)> SetAccountRawCalls { get; } = [];
        public int DisposeCount { get; private set; }

        public void SelfDestruct(Address addr) { }
        public void SetAccount(Address addr, Account? account) { }
        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value) { }
        public void SetStateTrieNode(in TreePath path, ReadOnlySpan<byte> rlp) { }
        public void SetStorageTrieNode(Hash256 address, in TreePath path, ReadOnlySpan<byte> rlp) { }
        public void SetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, in SlotValue? value) { }
        public void SetAccountRaw(in ValueHash256 addrHash, Account account) => SetAccountRawCalls.Add((addrHash, account));
        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) { }
        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) { }
        public void DeleteStateTrieNodeRange(in TreePath fromPath, in TreePath toPath) { }
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in TreePath fromPath, in TreePath toPath) { }
        public void Dispose() => DisposeCount++;
    }

    [Test]
    public void StateTree_DoubleWriteCheck_ThrowsWhenAccountAlreadyPresent()
    {
        IPersistence.IPersistenceReader reader = Reader();
        reader.GetAccountRaw(Arg.Any<ValueHash256>()).Returns([0x01]);
        using FlatSnapStateTree tree = NewStateTree(reader, enableDoubleWriteCheck: true);

        tree.BulkSetAndUpdateRootHash([new PathWithAccount(PathHash("11"), new Account(1, 100))]);

        Assert.That(() => tree.Commit(new ValueHash256(Bytes.FromHexString("ff" + new string('f', 62)))),
            Throws.Exception.With.Message.Contain("Double account flat write"));
    }
}
