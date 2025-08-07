// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;
using AccountRange = Nethermind.State.Snap.AccountRange;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapProviderTests
{
    [Test]
    public void AddAccountRange_AccountListIsEmpty_ThrowArgumentException()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestSynchronizerModule(new TestSyncConfig()))
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();

        Assert.That(
            () => snapProvider.AddAccountRange(
                0,
                Keccak.Zero,
                Keccak.Zero,
                Array.Empty<PathWithAccount>(),
                Array.Empty<byte[]>().AsReadOnly()), Throws.ArgumentException);
    }

    [Test]
    public void AddAccountRange_AccountsNotSorted_ReturnsInvalidOrder()
    {
        // Create accounts in wrong order (B before A)
        var accounts = new List<PathWithAccount>
        {
            new(TestItem.KeccakB, TestItem.GenerateRandomAccount()),
            new(TestItem.KeccakA, TestItem.GenerateRandomAccount())
        };

        using var trieStore = new TestRawTrieStore(new TestMemDb());
        var stateTree = new StateTree(trieStore.GetTrieStore(null), LimboLogs.Instance);

        // Don't actually add to tree, just test the validation logic
        var result = SnapProviderHelper.AddAccountRange(
            stateTree,
            0,
            Keccak.Zero,
            Keccak.Zero,
            Keccak.MaxValue,
            accounts);

        result.result.Should().Be(AddRangeResult.InvalidOrder);
    }

    [Test]
    public void AddAccountRange_AccountsOutOfBounds_ReturnsOutOfBounds()
    {
        // Create accounts outside the specified range
        var accounts = new List<PathWithAccount>
        {
            new(TestItem.KeccakA, TestItem.GenerateRandomAccount()),
            new(TestItem.KeccakB, TestItem.GenerateRandomAccount())
        };

        using var trieStore = new TestRawTrieStore(new TestMemDb());
        var stateTree = new StateTree(trieStore.GetTrieStore(null), LimboLogs.Instance);

        // Test with startingHash after all accounts
        var result = SnapProviderHelper.AddAccountRange(
            stateTree,
            0,
            Keccak.Zero,
            TestItem.KeccakC, // startingHash after accounts
            Keccak.MaxValue,
            accounts);

        result.result.Should().Be(AddRangeResult.OutOfBounds);
    }

    [Test]
    public void AddStorageRange_SlotsNotSorted_ReturnsInvalidOrder()
    {
        // Create slots in wrong order
        var slots = new List<PathWithStorageSlot>
        {
            new(TestItem.KeccakB, new byte[] { 0x01 }),
            new(TestItem.KeccakA, new byte[] { 0x02 })
        };

        var account = new PathWithAccount(TestItem.KeccakA, TestItem.GenerateRandomAccount());

        using var trieStore = new TestRawTrieStore(new TestMemDb());
        var storageTree = new StorageTree(trieStore.GetTrieStore(null), LimboLogs.Instance);

        var result = SnapProviderHelper.AddStorageRange(
            storageTree,
            account,
            slots,
            null,
            null);

        result.result.Should().Be(AddRangeResult.InvalidOrder);
    }

    [Test]
    public void AddStorageRange_SlotsOutOfBounds_ReturnsOutOfBounds()
    {
        // Create slots outside the specified range
        var slots = new List<PathWithStorageSlot>
        {
            new(TestItem.KeccakA, new byte[] { 0x01 }),
            new(TestItem.KeccakB, new byte[] { 0x02 })
        };

        var account = new PathWithAccount(TestItem.KeccakA, TestItem.GenerateRandomAccount());

        using var trieStore = new TestRawTrieStore(new TestMemDb());
        var storageTree = new StorageTree(trieStore.GetTrieStore(null), LimboLogs.Instance);

        var result = SnapProviderHelper.AddStorageRange(
            storageTree,
            account,
            slots,
            TestItem.KeccakC, // startingHash after slots
            Keccak.MaxValue);

        result.result.Should().Be(AddRangeResult.OutOfBounds);
    }

    [Test]
    public void AddStorageRange_EmptySlotsList_ThrowsArgumentException()
    {
        var account = new PathWithAccount(TestItem.KeccakA, TestItem.GenerateRandomAccount());

        using var trieStore = new TestRawTrieStore(new TestMemDb());
        var storageTree = new StorageTree(trieStore.GetTrieStore(null), LimboLogs.Instance);

        Assert.That(
            () => SnapProviderHelper.AddStorageRange(
                storageTree,
                account,
                Array.Empty<PathWithStorageSlot>(),
                null,
                null), Throws.ArgumentException);
    }

    [Test]
    public void AddAccountRange_ResponseHasEmptyListOfAccountsAndOneProof_ReturnsExpiredRootHash()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestSynchronizerModule(new TestSyncConfig()))
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();

        using AccountsAndProofs accountsAndProofs = new();
        AccountRange accountRange = new(Keccak.Zero, Keccak.Zero, Keccak.MaxValue);
        accountsAndProofs.PathAndAccounts = new List<PathWithAccount>().ToPooledList();
        accountsAndProofs.Proofs = new List<byte[]> { new byte[] { 0x0 } }.ToPooledList();

        snapProvider.AddAccountRange(accountRange, accountsAndProofs).Should().Be(AddRangeResult.ExpiredRootHash);
    }

    [Test]
    public void AddAccountRange_SetStartRange_ToAfterLastPath()
    {
        (Hash256, Account)[] entries =
        [
            (TestItem.KeccakA, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakB, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakC, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakD, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakE, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakF, TestItem.GenerateRandomAccount()),
        ];
        Array.Sort(entries, static (e1, e2) => e1.Item1.CompareTo(e2.Item1));

        (SnapServer ss, Hash256 root) = BuildSnapServerFromEntries(entries);

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestSynchronizerModule(new TestSyncConfig()
            {
                SnapSyncAccountRangePartitionCount = 1
            }))
            .WithSuggestedHeaderOfStateRoot(root)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        (IOwnedReadOnlyList<PathWithAccount> accounts, IOwnedReadOnlyList<byte[]> proofs) = ss.GetAccountRanges(
            root, Keccak.Zero, entries[3].Item1, 1.MB(), default);

        progressTracker.IsFinished(out SnapSyncBatch? batch).Should().Be(false);

        using AccountsAndProofs accountsAndProofs = new();
        accountsAndProofs.PathAndAccounts = accounts;
        accountsAndProofs.Proofs = proofs;

        snapProvider.AddAccountRange(batch?.AccountRangeRequest!, accountsAndProofs).Should().Be(AddRangeResult.OK);
        progressTracker.IsFinished(out batch).Should().Be(false);
        batch?.AccountRangeRequest?.StartingHash.Should().BeGreaterThan(entries[3].Item1);
        batch?.AccountRangeRequest?.StartingHash.Should().BeLessThan(entries[4].Item1);
    }

    [Test]
    public void AddAccountRange_ShouldNotStoreStorageAfterLimit()
    {
        (Hash256, Account)[] entries =
        [
            (TestItem.KeccakA, TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            (TestItem.KeccakB, TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            (TestItem.KeccakC, TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            (TestItem.KeccakD, TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            (TestItem.KeccakE, TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            (TestItem.KeccakF, TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
        ];
        Array.Sort(entries, static (e1, e2) => e1.Item1.CompareTo(e2.Item1));

        (SnapServer ss, Hash256 root) = BuildSnapServerFromEntries(entries);

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestSynchronizerModule(new TestSyncConfig()
            {
                SnapSyncAccountRangePartitionCount = 2
            }))
            .WithSuggestedHeaderOfStateRoot(root)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        (IOwnedReadOnlyList<PathWithAccount> accounts, IOwnedReadOnlyList<byte[]> proofs) = ss.GetAccountRanges(
            root, Keccak.Zero, Keccak.MaxValue, 1.MB(), default);

        progressTracker.IsFinished(out SnapSyncBatch? batch).Should().Be(false);

        using AccountsAndProofs accountsAndProofs = new();
        accountsAndProofs.PathAndAccounts = accounts;
        accountsAndProofs.Proofs = proofs;

        snapProvider.AddAccountRange(batch?.AccountRangeRequest!, accountsAndProofs).Should().Be(AddRangeResult.OK);

        container.ResolveNamed<IDb>(DbNames.State).GetAllKeys().Count().Should().Be(6);
    }

    [TestCase("badreq-roothash.zip")]
    [TestCase("badreq-roothash-2.zip")]
    [TestCase("badreq-roothash-3.zip")]
    [TestCase("badreq-trieexception.zip")]
    public void Test_EdgeCases(string testFileName)
    {
        using DeflateStream decompressor =
            new DeflateStream(
                GetType().Assembly
                    .GetManifestResourceStream($"Nethermind.Synchronization.Test.SnapSync.TestFixtures.{testFileName}")!,
                CompressionMode.Decompress);
        BadReq asReq = JsonSerializer.Deserialize<BadReq>(decompressor)!;
        AccountDecoder acd = new AccountDecoder();
        Account[] accounts = asReq.Accounts.Select((bt) => acd.Decode(new RlpStream(Bytes.FromHexString(bt)))!).ToArray();
        ValueHash256[] paths = asReq.Paths.Select((bt) => new ValueHash256(Bytes.FromHexString(bt))).ToArray();
        List<PathWithAccount> pathWithAccounts = accounts.Select((acc, idx) => new PathWithAccount(paths[idx], acc)).ToList();
        List<byte[]> proofs = asReq.Proofs.Select((str) => Bytes.FromHexString(str)).ToList();

        StateTree stree = new StateTree(new TestRawTrieStore(new TestMemDb()), LimboLogs.Instance);
        SnapProviderHelper.AddAccountRange(
                stree,
                0,
                new ValueHash256(asReq.Root),
                new ValueHash256(asReq.StartingHash),
                new ValueHash256(asReq.LimitHash),
                pathWithAccounts,
                proofs).result.Should().Be(AddRangeResult.OK);
    }

    private record BadReq(
        string Root,
        string StartingHash,
        string LimitHash,
        List<string> Proofs,
        List<string> Paths,
        List<string> Accounts
    );

    private static (SnapServer, Hash256) BuildSnapServerFromEntries((Hash256, Account)[] entries)
    {
        TestMemDb stateDb = new TestMemDb();
        TestRawTrieStore trieStore = new TestRawTrieStore(stateDb);
        StateTree st = new StateTree(trieStore, LimboLogs.Instance);
        {
            using var _ = trieStore.BeginBlockCommit(0);
            foreach (var entry in entries)
            {
                st.Set(entry.Item1, entry.Item2);
            }
            st.Commit();
        }

        IStateReader stateRootTracker = Substitute.For<IStateReader>();
        stateRootTracker.HasStateForBlock(Build.A.BlockHeader.WithStateRoot(st.RootHash).TestObject).Returns(true);
        var ss = new SnapServer(trieStore.AsReadOnly(), new TestMemDb(), stateRootTracker, LimboLogs.Instance);
        return (ss, st.RootHash);
    }
}
