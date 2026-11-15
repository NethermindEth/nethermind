// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapRangeServingTests
{
    private const long RequestId = 7;
    private const int SlotsPerContract = 300;
    private static readonly ValueHash256 MidRange = new("0x8000000000000000000000000000000000000000000000000000000000000000");

    private readonly AccountRangeMessageSerializer _accountSerializer = new();
    private readonly StorageRangesMessageSerializer _storageSerializer = new();
    private readonly Dictionary<ValueHash256, Account> _accounts = [];
    private readonly List<PathWithAccount> _contracts = [];
    private PathWithAccount _externallyOwned = null!;
    private TestRawTrieStore _store = null!;
    private SnapStateServer _server = null!;
    private Hash256 _rootHash = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new TestRawTrieStore(new MemDb());
        StateTree tree = new(_store, LimboLogs.Instance);
        for (int i = 0; i < 200; i++)
        {
            Account account = (i % 5) switch
            {
                0 => new Account((ulong)i, (UInt256)i),
                1 => new Account(ulong.MaxValue - (ulong)i, UInt256.MaxValue - (UInt256)i),
                2 => new Account(1, 0, Keccak.EmptyTreeHash, Keccak.Compute([(byte)i])),
                3 => new Account(0, (UInt256)i * 1_000_000_007, Keccak.Compute([(byte)i, 1]), Keccak.OfAnEmptyString),
                _ => new Account(0, 0),
            };
            SetAccount(tree, Keccak.Compute(BitConverter.GetBytes(i)), account);
        }

        for (int contract = 0; contract < 3; contract++)
        {
            Hash256 path = Keccak.Compute(BitConverter.GetBytes(10_000 + contract));
            StorageTree storageTree = new(_store.GetTrieStore(path), LimboLogs.Instance);
            byte[] value = new byte[32];
            for (int slot = 0; slot < SlotsPerContract; slot++)
            {
                Span<byte> slotValue = value.AsSpan(0, 1 + (slot + contract) % 32);
                slotValue.Fill((byte)(slot % 255 + 1));
                storageTree.Set((UInt256)slot, slotValue);
            }

            storageTree.Commit();
            Account account = new(1, 0, storageTree.RootHash, Keccak.Compute(path.Bytes));
            SetAccount(tree, path, account);
            _contracts.Add(new PathWithAccount(path, account));
        }

        Hash256 externallyOwnedPath = Keccak.Compute(BitConverter.GetBytes(0));
        _externallyOwned = new PathWithAccount(externallyOwnedPath, _accounts[externallyOwnedPath]);

        tree.Commit();
        _rootHash = tree.RootHash;
        _server = new SnapStateServer(_store.AsReadOnly(), LimboLogs.Instance);
    }

    private void SetAccount(StateTree tree, Hash256 path, Account account)
    {
        tree.Set(path, account);
        _accounts[path] = account;
    }

    [TearDown]
    public void TearDown() => ((IDisposable)_store).Dispose();

    private static IEnumerable<TestCaseData> AccountRanges()
    {
        yield return new TestCaseData(ValueKeccak.Zero, 4000L).SetName("From start, partial");
        yield return new TestCaseData(ValueKeccak.Zero, 1_000_000L).SetName("From start, whole state");
        yield return new TestCaseData(MidRange, 4000L).SetName("From middle, partial");
        yield return new TestCaseData(ValueKeccak.MaxValue, 4000L).SetName("Past the last account");
    }

    [TestCaseSource(nameof(AccountRanges))]
    public void Account_range_reply_matches_the_account_object_encoding(ValueHash256 startingHash, long byteLimit)
    {
        (IOwnedReadOnlyList<PathWithAccount> served, IByteArrayList proofs) =
            _server.GetAccountRanges(_rootHash, startingHash, null, byteLimit, CancellationToken.None);
        Assert.That(served, Is.InstanceOf<SlimAccountRange>());
        SlimAccountRange slim = (SlimAccountRange)served;

        using ArrayPoolList<PathWithAccount> objects = new(slim.Count);
        for (int i = 0; i < slim.Count; i++)
        {
            ValueHash256 path = slim.GetPath(i);
            objects.Add(new PathWithAccount(path, _accounts[path]));
        }

        AccountRangeMessage expected = new() { RequestId = RequestId, PathsWithAccounts = objects, Proofs = proofs };
        using AccountRangeMessage actual = new() { RequestId = RequestId, PathsWithAccounts = slim, Proofs = proofs };

        Assert.That(Serialize(actual), Is.EqualTo(Serialize(expected)));
    }

    [Test]
    public void Account_range_view_decodes_the_served_accounts()
    {
        (IOwnedReadOnlyList<PathWithAccount> served, IByteArrayList proofs) =
            _server.GetAccountRanges(_rootHash, ValueKeccak.Zero, null, 1_000_000, CancellationToken.None);
        using (served)
        using (proofs)
        {
            Assert.That(served.Count, Is.EqualTo(_accounts.Count));
            foreach (PathWithAccount pathWithAccount in served)
            {
                Assert.That(pathWithAccount.Account, Is.EqualTo(_accounts[pathWithAccount.Path]));
            }
        }
    }

    private static IEnumerable<TestCaseData> StorageRanges()
    {
        yield return new TestCaseData(ValueKeccak.Zero, 1_000_000L, 3).SetName("All contracts from start");
        yield return new TestCaseData(ValueKeccak.Zero, 12_000L, 3).SetName("Contracts from start, byte limit");
        yield return new TestCaseData(MidRange, 4000L, 1).SetName("One contract from middle");
        yield return new TestCaseData(ValueKeccak.Zero, 1_000_000L, 0).SetName("Account without storage");
    }

    [TestCaseSource(nameof(StorageRanges))]
    public void Storage_range_reply_matches_the_slot_array_encoding(ValueHash256 startingHash, long byteLimit, int contracts)
    {
        PathWithAccount[] accounts = contracts == 0 ? [_externallyOwned] : _contracts.GetRange(0, contracts).ToArray();
        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> served, IByteArrayList? proofs) =
            _server.GetStorageRanges(_rootHash, accounts, startingHash, null, byteLimit, CancellationToken.None);
        Assert.That(served, Is.InstanceOf<StorageSlotRanges>());

        using ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> arrays = new(served.Count);
        for (int i = 0; i < served.Count; i++)
        {
            StorageSlotRanges.AccountSlots slots = (StorageSlotRanges.AccountSlots)served[i];
            ArrayPoolList<PathWithStorageSlot> copy = new(slots.Count);
            for (int j = 0; j < slots.Count; j++)
            {
                copy.Add(new PathWithStorageSlot(in slots.GetPath(j), slots.GetValue(j).ToArray()));
            }

            arrays.Add(copy);
        }

        StorageRangeMessage expected = new() { RequestId = RequestId, Slots = arrays, Proofs = proofs! };
        using StorageRangeMessage actual = new() { RequestId = RequestId, Slots = served, Proofs = proofs! };

        Assert.That(Serialize(actual), Is.EqualTo(Serialize(expected)));
        foreach (IOwnedReadOnlyList<PathWithStorageSlot> copy in arrays) copy.Dispose();
    }

    [Test]
    public void Storage_range_values_match_the_trie_leaves()
    {
        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> served, IByteArrayList? proofs) =
            _server.GetStorageRanges(_rootHash, [_contracts[0]], ValueKeccak.Zero, null, 1_000_000, CancellationToken.None);
        try
        {
            StorageTree storageTree = new(_store.GetTrieStore(_contracts[0].Path.ToCommitment()), _contracts[0].Account!.StorageRoot, LimboLogs.Instance);
            Assert.That(served[0].Count, Is.EqualTo(SlotsPerContract));
            foreach (PathWithStorageSlot slot in served[0])
            {
                Assert.That(slot.SlotRlpValue, Is.EqualTo(storageTree.Get(slot.Path.Bytes).ToArray()));
            }
        }
        finally
        {
            served.DisposeRecursive();
            proofs?.Dispose();
        }
    }

    [Test]
    public void Account_leaf_without_account_is_rejected()
    {
        AccountCollector collector = new();
        using SlimAccountRange accounts = collector.Accounts;
        Assert.That(() => collector.Collect(ValueKeccak.Zero, [0xc1, 0x80]), Throws.InstanceOf<TrieException>());
        Assert.That(accounts.Count, Is.Zero);
    }

    [Test]
    public void Account_range_returns_its_buffer_on_dispose()
    {
        TrackingPool pool = new();
        SlimAccountRange accounts = new(pool);
        byte[] leaf = AccountDecoder.Instance.Encode(new Account(5, 1_000_000, Keccak.Compute("storage"u8), Keccak.Compute("code"u8)));
        for (int i = 0; i < 1000; i++) accounts.AddLeaf(Keccak.Compute(BitConverter.GetBytes(i)), leaf);

        Assert.That(pool.Outstanding, Is.GreaterThan(0));
        accounts.Dispose();
        Assert.That(pool.Outstanding, Is.Zero);
    }

    [Test]
    public void Storage_ranges_return_their_buffer_on_dispose()
    {
        TrackingPool pool = new();
        StorageSlotRanges slots = new(2, pool);
        for (int account = 0; account < 2; account++)
        {
            for (int i = 0; i < 1000; i++) slots.AddSlot(Keccak.Compute(BitConverter.GetBytes(i)), [0x82, (byte)i, 1]);
            slots.TryCompleteAccount();
        }

        Assert.That(pool.Outstanding, Is.GreaterThan(0));
        slots.DisposeRecursive();
        Assert.That(pool.Outstanding, Is.Zero);
    }

    [Test]
    public void Account_slots_throw_when_read_after_their_parent_is_disposed()
    {
        StorageSlotRanges slots = new(2);
        for (int account = 0; account < 2; account++)
        {
            slots.AddSlot(Keccak.Compute(BitConverter.GetBytes(account)), [0x82, (byte)account, 1]);
            slots.TryCompleteAccount();
        }

        StorageSlotRanges.AccountSlots materialized = (StorageSlotRanges.AccountSlots)slots[0];
        PathWithStorageSlot copy = materialized[0];
        StorageSlotRanges.AccountSlots view = (StorageSlotRanges.AccountSlots)slots[1];
        slots.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => view.GetValue(0).Length, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => view.GetPath(0), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => view[0], Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => view.AsSpan().Length, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(materialized[0].SlotRlpValue, Is.SameAs(copy.SlotRlpValue));
            Assert.That(materialized[0].SlotRlpValue, Is.EqualTo(new byte[] { 0x82, 0, 1 }));
        }
    }

    private byte[] Serialize(AccountRangeMessage message)
    {
        byte[] bytes = new byte[_accountSerializer.GetLength(message)];
        Assert.That(_accountSerializer.Serialize(bytes, message), Is.EqualTo(bytes.Length));
        return bytes;
    }

    private byte[] Serialize(StorageRangeMessage message)
    {
        byte[] bytes = new byte[_storageSerializer.GetLength(message)];
        Assert.That(_storageSerializer.Serialize(bytes, message), Is.EqualTo(bytes.Length));
        return bytes;
    }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        public int Outstanding { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            Outstanding++;
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false) => Outstanding--;
    }
}
