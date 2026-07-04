// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test;

/// <summary>
/// Pins <see cref="BalStateRootCalculator.ComputeRoot"/>: for every scenario the root it derives from a
/// BAL delta must equal the root produced by applying the same changes directly to a state/storage trie.
/// Also guards the EIP-161 deletion rule (storage root not consulted) and the read-only no-persistence contract.
/// </summary>
[TestFixture]
public class BalStateRootCalculatorTests
{
    private static readonly byte[] SomeCode = [0x60, 0x00, 0x60, 0x01, 0x50];
    private static readonly byte[] OtherCode = [0x60, 0x02, 0x60, 0x03, 0x50, 0x50];

    /// <summary>
    /// One account's pre-state and the changes applied to it, plus the equivalent BAL account-changes builder.
    /// A scenario applies its pre-state (committed) and post-state (direct apply, for the expected root) with the
    /// same primitives the calculator uses, so the expected root is authoritative.
    /// </summary>
    private sealed class AccountScenario
    {
        public required Address Address { get; init; }
        public Account? Pre { get; init; }
        public (UInt256 slot, UInt256 value)[] PreStorage { get; init; } = [];

        public Account? Post { get; init; }
        public (UInt256 slot, UInt256 value)[] PostStorage { get; init; } = [];

        public required ReadOnlyAccountChanges BalChanges { get; init; }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static ITrieStore NewStore() => TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance);

    /// <summary>Applies pre-storage to a fresh storage tree over <paramref name="store"/>, commits, returns its root.</summary>
    private static Hash256 BuildStorage(ITrieStore store, Address address, (UInt256 slot, UInt256 value)[] slots)
    {
        StorageTree tree = new(store.GetTrieStore(address), LimboLogs.Instance);
        foreach ((UInt256 slot, UInt256 value) in slots)
        {
            tree.Set(slot, value.IsZero ? [] : value.ToBigEndian().WithoutLeadingZeros().ToArray());
        }
        tree.Commit();
        return tree.RootHash;
    }

    /// <summary>Applies storage on top of an existing storage root and returns the new root (no commit needed for expected).</summary>
    private static Hash256 ApplyStorage(ITrieStore store, Address address, Hash256 preRoot, (UInt256 slot, UInt256 value)[] slots)
    {
        StorageTree tree = new(store.GetTrieStore(address), preRoot, LimboLogs.Instance);
        foreach ((UInt256 slot, UInt256 value) in slots)
        {
            tree.Set(slot, value.IsZero ? [] : value.ToBigEndian().WithoutLeadingZeros().ToArray());
        }
        tree.UpdateRootHash(canBeParallel: false);
        return tree.RootHash;
    }

    /// <summary>
    /// Builds the committed pre-state root and the expected post-state root over a shared store from the scenarios,
    /// resolving each account's storage root from its own storage sub-trie. Returns the parent header and expected root.
    /// </summary>
    private static (BlockHeader parent, Hash256 expectedRoot) BuildFixture(ITrieStore store, IReadOnlyList<AccountScenario> scenarios)
    {
        // Pre-state: commit each account (with its pre-storage root) into a committed state tree.
        StateTree preTree = new(store.GetTrieStore(null), LimboLogs.Instance);
        foreach (AccountScenario s in scenarios)
        {
            if (s.Pre is null) continue;
            Hash256 preStorageRoot = BuildStorage(store, s.Address, s.PreStorage);
            preTree.Set(s.Address, s.Pre.WithChangedStorageRoot(preStorageRoot));
        }
        preTree.Commit();
        Hash256 preRoot = preTree.RootHash;

        // Expected post-state: re-open the committed state root, apply each account's post-state directly.
        StateTree postTree = new(store.GetTrieStore(null), LimboLogs.Instance);
        postTree.SetRootHash(preRoot, true);
        foreach (AccountScenario s in scenarios)
        {
            if (s.Post is null)
            {
                postTree.Set(s.Address, null);
                continue;
            }

            Hash256 preStorageRoot = s.Pre is null ? Keccak.EmptyTreeHash : BuildStorage(store, s.Address, s.PreStorage);
            Hash256 postStorageRoot = s.PostStorage.Length == 0
                ? preStorageRoot // no storage writes: the account keeps its actual pre-state storage root
                : ApplyStorage(store, s.Address, preStorageRoot, s.PostStorage);
            postTree.Set(s.Address, s.Post.WithChangedStorageRoot(postStorageRoot));
        }
        postTree.UpdateRootHash(canBeParallel: false);
        Hash256 expectedRoot = postTree.RootHash;

        BlockHeader parent = Build.A.BlockHeader.WithStateRoot(preRoot).TestObject;
        return (parent, expectedRoot);
    }

    private static ReadOnlyBlockAccessList Bal(params AccountScenario[] scenarios)
    {
        ReadOnlyAccountChanges[] changes = new ReadOnlyAccountChanges[scenarios.Length];
        for (int i = 0; i < scenarios.Length; i++) changes[i] = scenarios[i].BalChanges;
        return Build.A.BlockAccessList.WithAccountChanges(changes).TestObject;
    }

    private static void AssertComputesExpectedRoot(params AccountScenario[] scenarios)
    {
        // Expected root and calculator run over separate stores backed by identical committed pre-state,
        // so the calculator's node mutation cannot influence the expected computation.
        ITrieStore expectedStore = NewStore();
        (BlockHeader parent, Hash256 expectedRoot) = BuildFixture(expectedStore, scenarios);

        ITrieStore calcStore = NewStore();
        BuildFixture(calcStore, scenarios); // seed the same committed pre-state into the calculator's store

        BalStateRootCalculator calculator = new(calcStore, LimboLogs.Instance);
        Hash256 actual = calculator.ComputeRoot(parent, BalPostStateDelta.Reduce(Bal(scenarios)));

        Assert.That(actual, Is.EqualTo(expectedRoot));
    }

    // ---- tests ---------------------------------------------------------------------------------------------

    [Test]
    public void T2_1_balance_only_change_on_existing_account()
    {
        AccountScenario s = new()
        {
            Address = TestItem.AddressA,
            Pre = new Account(1, 100),
            Post = new Account(1, 500),
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, 500))
                .TestObject,
        };

        AssertComputesExpectedRoot(s);
    }

    [Test]
    public void T2_2_nonce_only_change_on_existing_account()
    {
        AccountScenario s = new()
        {
            Address = TestItem.AddressA,
            Pre = new Account(1, 100),
            Post = new Account(9, 100),
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithNonceChanges(new NonceChange(0, 9))
                .TestObject,
        };

        AssertComputesExpectedRoot(s);
    }

    [Test]
    public void T2_3_new_account_creation()
    {
        AccountScenario s = new()
        {
            Address = TestItem.AddressA,
            Pre = null,
            Post = new Account(0, 1000),
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, 1000))
                .TestObject,
        };

        AssertComputesExpectedRoot(s);
    }

    [Test]
    public void T2_4_contract_creation_code_and_storage_on_fresh_account()
    {
        ValueHash256 codeHash = ValueKeccak.Compute(SomeCode);
        AccountScenario s = new()
        {
            Address = TestItem.AddressA,
            Pre = null,
            Post = new Account(1, 50, Keccak.EmptyTreeHash, new Hash256(codeHash)),
            PostStorage = [(1, 111), (2, 222)],
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithNonceChanges(new NonceChange(0, 1))
                .WithBalanceChanges(new BalanceChange(0, 50))
                .WithCodeChanges(new CodeChange(0, SomeCode))
                .WithStorageChanges(1, new StorageChange(0, (UInt256)111))
                .WithStorageChanges(2, new StorageChange(0, (UInt256)222))
                .TestObject,
        };

        AssertComputesExpectedRoot(s);
    }

    [Test]
    public void T2_5_storage_write_on_existing_contract_with_existing_storage()
    {
        ValueHash256 codeHash = ValueKeccak.Compute(SomeCode);
        Account contract = new(1, 50, Keccak.EmptyTreeHash, new Hash256(codeHash));
        AccountScenario s = new()
        {
            Address = TestItem.AddressA,
            Pre = contract,
            PreStorage = [(1, 111), (2, 222)],
            Post = contract,
            PostStorage = [(3, 333)],
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageChanges(3, new StorageChange(0, (UInt256)333))
                .TestObject,
        };

        AssertComputesExpectedRoot(s);
    }

    [Test]
    public void T2_6_write_to_zero_deleting_only_slot_returns_storage_root_to_empty()
    {
        ValueHash256 codeHash = ValueKeccak.Compute(SomeCode);
        Account contract = new(1, 50, Keccak.EmptyTreeHash, new Hash256(codeHash));
        AccountScenario s = new()
        {
            Address = TestItem.AddressA,
            Pre = contract,
            PreStorage = [(7, 777)],
            Post = contract,
            PostStorage = [(7, 0)],
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageChanges(7, new StorageChange(0, UInt256.Zero))
                .TestObject,
        };

        AssertComputesExpectedRoot(s);
    }

    [Test]
    public void T2_7_write_to_zero_of_one_of_several_slots()
    {
        ValueHash256 codeHash = ValueKeccak.Compute(SomeCode);
        Account contract = new(1, 50, Keccak.EmptyTreeHash, new Hash256(codeHash));
        AccountScenario s = new()
        {
            Address = TestItem.AddressA,
            Pre = contract,
            PreStorage = [(1, 111), (2, 222), (3, 333)],
            Post = contract,
            PostStorage = [(2, 0)],
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageChanges(2, new StorageChange(0, UInt256.Zero))
                .TestObject,
        };

        AssertComputesExpectedRoot(s);
    }

    [Test]
    public void T2_8_account_drained_to_empty_no_storage_deletes_leaf()
    {
        AccountScenario s = new()
        {
            Address = TestItem.AddressA,
            Pre = new Account(0, 1000),
            Post = null, // balance -> 0, nonce 0, no code => IsEmpty => deleted
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, UInt256.Zero))
                .TestObject,
        };

        AssertComputesExpectedRoot(s);
    }

    [Test]
    public void T2_9_multi_account_create_change_and_delete()
    {
        AccountScenario create = new()
        {
            Address = TestItem.AddressA,
            Pre = null,
            Post = new Account(0, 1000),
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, 1000))
                .TestObject,
        };
        ValueHash256 codeHash = ValueKeccak.Compute(SomeCode);
        Account contract = new(1, 50, Keccak.EmptyTreeHash, new Hash256(codeHash));
        AccountScenario change = new()
        {
            Address = TestItem.AddressB,
            Pre = contract,
            PreStorage = [(1, 111)],
            Post = contract,
            PostStorage = [(2, 222)],
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressB)
                .WithStorageChanges(2, new StorageChange(0, (UInt256)222))
                .TestObject,
        };
        AccountScenario delete = new()
        {
            Address = TestItem.AddressC,
            Pre = new Account(0, 300),
            Post = null,
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressC)
                .WithBalanceChanges(new BalanceChange(0, UInt256.Zero))
                .TestObject,
        };

        AssertComputesExpectedRoot(create, change, delete);
    }

    [Test]
    public void T2_10_compute_root_performs_zero_db_writes()
    {
        // Build committed pre-state, then flip the DB into write-recording mode and assert ComputeRoot writes nothing.
        WriteRecordingMemDb db = new();
        ITrieStore store = TestTrieStoreFactory.Build(db, LimboLogs.Instance);

        ValueHash256 codeHash = ValueKeccak.Compute(SomeCode);
        Account contract = new(1, 50, Keccak.EmptyTreeHash, new Hash256(codeHash));
        AccountScenario s = new()
        {
            Address = TestItem.AddressA,
            Pre = contract,
            PreStorage = [(1, 111)],
            Post = contract,
            PostStorage = [(2, 222)],
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageChanges(2, new StorageChange(0, (UInt256)222))
                .TestObject,
        };
        (BlockHeader parent, _) = BuildFixture(store, [s]);

        BalStateRootCalculator calculator = new(store, LimboLogs.Instance);
        db.StartRecording();
        calculator.ComputeRoot(parent, BalPostStateDelta.Reduce(Bal(s)));

        Assert.That(db.RecordedWrites, Is.Zero, "ComputeRoot must never persist");
    }

    [Test]
    public void T2_11_storage_slot_key_above_2_pow_64()
    {
        UInt256 bigSlot = UInt256.Parse("18446744073709551617"); // 2^64 + 1
        ValueHash256 codeHash = ValueKeccak.Compute(SomeCode);
        Account contract = new(1, 50, Keccak.EmptyTreeHash, new Hash256(codeHash));
        AccountScenario s = new()
        {
            Address = TestItem.AddressA,
            Pre = contract,
            Post = contract,
            PostStorage = [(bigSlot, 999)],
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageChanges(bigSlot, new StorageChange(0, (UInt256)999))
                .TestObject,
        };

        AssertComputesExpectedRoot(s);
    }

    [Test]
    public void T2_12_unchanged_storage_account_keeps_pre_storage_root()
    {
        ValueHash256 codeHash = ValueKeccak.Compute(SomeCode);
        Account contract = new(1, 50, Keccak.EmptyTreeHash, new Hash256(codeHash));
        AccountScenario s = new()
        {
            Address = TestItem.AddressA,
            Pre = contract,
            PreStorage = [(1, 111), (2, 222)],
            Post = new Account(2, 50, Keccak.EmptyTreeHash, new Hash256(codeHash)), // only nonce changes
            PostStorage = [], // no storage writes: storage root must stay
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithNonceChanges(new NonceChange(0, 2))
                .TestObject,
        };

        AssertComputesExpectedRoot(s);
    }

    [Test]
    public void T2_13_empty_reduced_account_with_residual_storage_is_deleted()
    {
        // EIP-161: an account reduced to empty (nonce 0, balance 0, no code) is deleted even though it
        // still has on-disk storage; the storage root must NOT block deletion (selfdestruct shape).
        ValueHash256 codeHash = ValueKeccak.Compute(SomeCode);
        AccountScenario s = new()
        {
            Address = TestItem.AddressA,
            Pre = new Account(1, 500, Keccak.EmptyTreeHash, new Hash256(codeHash)),
            PreStorage = [(1, 111), (2, 222)],
            Post = null, // reduced to empty => deleted, residual storage orphaned
            // BAL drains balance to 0, nonce to 0, and code to empty; storage untouched (still on disk).
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, UInt256.Zero))
                .WithNonceChanges(new NonceChange(0, 0))
                .WithCodeChanges(new CodeChange(0, []))
                .TestObject,
        };

        AssertComputesExpectedRoot(s);
    }

    [Test]
    public void T2_14_eip7702_code_only_change_keeps_storage_and_storage_root()
    {
        // EIP-7702-style delegation update: only the code hash changes on an account with existing storage.
        // Slots must survive and the storage root must be unchanged; only the code hash differs.
        ValueHash256 preCodeHash = ValueKeccak.Compute(SomeCode);
        ValueHash256 postCodeHash = ValueKeccak.Compute(OtherCode);
        AccountScenario s = new()
        {
            Address = TestItem.AddressA,
            Pre = new Account(1, 50, Keccak.EmptyTreeHash, new Hash256(preCodeHash)),
            PreStorage = [(1, 111), (2, 222)],
            Post = new Account(1, 50, Keccak.EmptyTreeHash, new Hash256(postCodeHash)),
            PostStorage = [], // no storage writes: slots {1,2} survive, storage root unchanged
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithCodeChanges(new CodeChange(0, OtherCode))
                .TestObject,
        };

        AssertComputesExpectedRoot(s);
    }

    [Test]
    public void T2_15_account_emptied_by_storage_clear_is_deleted()
    {
        // Pre-existing account that is ALREADY empty by scalar fields (nonce 0, balance 0, no code) but has
        // residual storage; the BAL's only change zeros its one slot. Account.IsEmpty ignores storage, so the
        // leaf is deleted - a wrong impl would keep an empty account with an empty storage root.
        AccountScenario s = new()
        {
            Address = TestItem.AddressA,
            Pre = new Account(0, UInt256.Zero, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString),
            PreStorage = [(7, 777)],
            Post = null, // still empty by scalars; clearing storage only makes it more so => deleted
            PostStorage = [(7, 0)],
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageChanges(7, new StorageChange(0, UInt256.Zero))
                .TestObject,
        };

        AssertComputesExpectedRoot(s);
    }

    [Test]
    public void T2_16_empty_parent_state_creates_fresh_accounts()
    {
        // Genesis-parent shape: parentStateRoot == EmptyTreeHash, no fixture pre-commit. Exercises the
        // emptyParent short-circuit in PASS A end to end.
        AccountScenario a = new()
        {
            Address = TestItem.AddressA,
            Pre = null,
            Post = new Account(0, 1000),
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, 1000))
                .TestObject,
        };
        AccountScenario b = new()
        {
            Address = TestItem.AddressB,
            Pre = null,
            Post = new Account(3, 2000),
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressB)
                .WithNonceChanges(new NonceChange(0, 3))
                .WithBalanceChanges(new BalanceChange(0, 2000))
                .TestObject,
        };

        AssertComputesExpectedRoot(a, b);
    }

    [Test]
    public void T2_17_absent_account_reducing_to_empty_is_a_noop_delete()
    {
        // Non-empty parent (anchor account). A second delta account does NOT exist in the parent trie and
        // reduces to empty (a lone zero-value storage write). Deleting a non-existent leaf is a no-op, so the
        // root is unchanged from the parent and no spurious leaf is created.
        AccountScenario anchor = new()
        {
            Address = TestItem.AddressA,
            Pre = new Account(1, 100),
            Post = new Account(1, 100), // unchanged; its BAL entry is read-only so Reduce excludes it
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads(0)
                .TestObject,
        };
        AccountScenario missing = new()
        {
            Address = TestItem.AddressB,
            Pre = null,
            Post = null, // never existed and reduces to empty => no-op delete
            BalChanges = Build.An.AccountChanges
                .WithAddress(TestItem.AddressB)
                .WithStorageChanges(1, new StorageChange(0, UInt256.Zero))
                .TestObject,
        };

        AssertComputesExpectedRoot(anchor, missing);
    }

    /// <summary>MemDb that, once <see cref="StartRecording"/> is called, counts every write for T2.10.</summary>
    private sealed class WriteRecordingMemDb : MemDb
    {
        private bool _recording;

        public int RecordedWrites { get; private set; }

        public void StartRecording() => _recording = true;

        public override void Set(System.ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (_recording) RecordedWrites++;
            base.Set(key, value, flags);
        }

        public override void Remove(System.ReadOnlySpan<byte> key)
        {
            if (_recording) RecordedWrites++;
            base.Remove(key);
        }
    }
}
