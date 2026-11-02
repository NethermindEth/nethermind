// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class GuestWorldStateTests
{
    private static readonly Address[] Addresses =
    [
        TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, TestItem.AddressE, TestItem.AddressF,
    ];

    private static readonly byte[][] Codes = [[1, 2, 3], [0x60, 0x00, 0x60, 0x00], [7]];

    private static IEnumerable<IReleaseSpec> Specs() => [Frontier.Instance, SpuriousDragon.Instance, Cancun.Instance];

    /// <summary>Drives a <see cref="GuestWorldState"/> and a <see cref="WorldState"/> through the same random operations.</summary>
    /// <remarks>
    /// Every read, every thrown exception and every state root must agree. The operations follow what execution does:
    /// where it never goes, <see cref="WorldState"/> has quirks that <see cref="GuestWorldState"/> need not share.
    /// </remarks>
    [Test]
    public void Matches_world_state([Range(0, 299)] int seed, [ValueSource(nameof(Specs))] IReleaseSpec spec)
    {
        (IWorldState expected, BlockHeader baseBlock) = CreatePreState(guest: false);
        (IWorldState actual, _) = CreatePreState(guest: true);

        using IDisposable expectedScope = expected.BeginScope(baseBlock);
        using IDisposable actualScope = actual.BeginScope(baseBlock);

        Random random = new(seed);
        List<(Snapshot Expected, Snapshot Actual)> snapshots = [];
        List<string> log = [];
        // Accounts whose storage the commit window cleared, and those of them with a revert since, by the transaction of
        // the revert. A clear in the same transaction lifts it. Execution commits each transaction, so it reads no
        // original past a clear reverted in an earlier transaction of the window, and the two states differ there.
        HashSet<Address> cleared = [];
        Dictionary<Address, int> clearReverted = [];
        int transaction = 0;
        for (int step = 0; step < 400; step++)
        {
            Address address = Addresses[random.Next(Addresses.Length)];
            StorageCell cell = new(Addresses[random.Next(3)], (UInt256)random.Next(5));
            UInt256 value = random.Next(3) == 0 ? UInt256.Zero : (UInt256)random.Next(1, 4);
            int operation = random.Next(27);
            log.Add($"{step}: {operation} {address} {cell} {value}");

            // Storage is read and written only in the context of a contract: an account with code, or with nonce 1
            // while its constructor runs. Neither is ever empty.
            if (operation is 5 or 6 or 7 or 23)
            {
                bool isContext = expected.IsNonZeroAccount(cell.Address, out _);
                Assert.That(actual.IsNonZeroAccount(cell.Address, out _), Is.EqualTo(isContext), Trace);
                if (!isContext) continue;
            }

            switch (operation)
            {
                case 0 or 1:
                    snapshots.Add((expected.TakeSnapshot(), actual.TakeSnapshot()));
                    break;
                case 2:
                    // A block builder restores to a transaction start, never past it.
                    snapshots.Clear();
                    snapshots.Add((expected.TakeSnapshot(newTransactionStart: true), actual.TakeSnapshot(newTransactionStart: true)));
                    transaction++;
                    break;
                case 3 when snapshots.Count != 0:
                    {
                        int index = random.Next(snapshots.Count);
                        log.Add($"restore {index}");
                        expected.Restore(snapshots[index].Expected);
                        actual.Restore(snapshots[index].Actual);
                        snapshots.RemoveRange(index + 1, snapshots.Count - index - 1);
                        foreach (Address clearedAddress in cleared) clearReverted[clearedAddress] = transaction;
                        break;
                    }
                case 4:
                    {
                        bool commitRoots = random.Next(4) == 0;
                        log.Add($"commit roots {commitRoots}");
                        expected.Commit(spec, NullStateTracer.Instance, commitRoots: commitRoots);
                        actual.Commit(spec, NullStateTracer.Instance, commitRoots: commitRoots);
                        snapshots.Clear();
                        cleared.Clear();
                        clearReverted.Clear();
                        if (random.Next(2) == 0)
                        {
                            expected.ResetTransient();
                            actual.ResetTransient();
                        }

                        if (commitRoots) AssertSameRoot();
                        break;
                    }
                case 5:
                    {
                        bool compareOriginal = !clearReverted.ContainsKey(cell.Address);
                        Compare(s =>
                        {
                            s.Get(cell, out UInt256 current);
                            s.GetOriginal(cell, out UInt256 original);
                            return compareOriginal ? $"{current} {original}" : $"{current}";
                        });
                        break;
                    }
                case 6 or 7:
                    {
                        bool compareOriginal = !clearReverted.ContainsKey(cell.Address);
                        Compare(s =>
                        {
                            s.Get(cell, out UInt256 current);
                            s.GetOriginal(cell, out UInt256 original);
                            s.Set(cell, value, current);
                            return compareOriginal ? $"{current} {original}" : $"{current}";
                        });
                        break;
                    }
                case 8:
                    Compare(s => { s.GetTransientState(cell, out UInt256 v); return v.ToString(); });
                    break;
                case 9:
                    Compare(s => { s.SetTransientState(cell, value); return ""; });
                    break;
                case 10:
                    Compare(s => Describe(s, address));
                    break;
                case 11:
                    Compare(s => { s.AddToBalance(address, value, spec, out UInt256 old); return old.ToString(); });
                    break;
                case 12:
                    Compare(s => { s.SubtractFromBalance(address, value, spec, out UInt256 old); return old.ToString(); });
                    break;
                case 13:
                    Compare(s => { s.IncrementNonce(address, 1, out ulong old); return old.ToString(); });
                    break;
                case 14:
                    {
                        ulong nonce = (ulong)random.Next(2);
                        // Execution creates only an account that does not exist, which it reads first.
                        Compare(s =>
                        {
                            bool existed = s.AccountExists(address);
                            if (!existed) s.CreateAccount(address, value, nonce);
                            return existed.ToString();
                        });
                        break;
                    }
                case 15:
                    // Execution clears an account's storage before it deletes the account.
                    Compare(s => { s.ClearStorage(address); s.DeleteAccount(address); return ""; });
                    cleared.Add(address);
                    LiftRevertedClear(address);
                    break;
                case 16:
                    Compare(s => { s.ClearStorage(cell.Address); return ""; });
                    cleared.Add(cell.Address);
                    LiftRevertedClear(cell.Address);
                    break;
                case 17:
                    {
                        byte[] code = Codes[random.Next(Codes.Length)];
                        Compare(s => { s.InsertCode(address, Keccak.Compute(code), code, spec); return ""; });
                        break;
                    }
                case 18:
                    Compare(s => s.GetOriginalBalance(address).ToString());
                    break;
                case 19:
                    Compare(s => { bool created = s.AddToBalanceAndCreateIfNotExists(address, value, spec, out UInt256 old); return $"{created} {old}"; });
                    break;
                case 20:
                    Compare(s => { bool nonZero = s.IsNonZeroAccount(address, out bool exists); return $"{nonZero} {exists}"; });
                    break;
                case 21:
                    Compare(s => { s.CreateAccountIfNotExists(address, value); return ""; });
                    break;
                case 22:
                    Compare(s => { s.SubtractFromBalanceAndSetNonce(address, value, 5, spec); return ""; });
                    break;
                case 23:
                    // As EIP-7906's storage assertion reads it: the value before a clear, else the live value.
                    Compare(s =>
                    {
                        s.Get(cell, out UInt256 live);
                        return (s.TryGetStorageBeforeClear(cell, out UInt256 before) ? before : live).ToString();
                    });
                    break;
                case 24:
                    Compare(s => s.GetCode(address).ToArray().ToHexString());
                    break;
                case 25:
                    Compare(s => { s.DecrementNonce(address, 1); return ""; });
                    break;
                case 26:
                    {
                        ulong nonce = (ulong)random.Next(3);
                        Compare(s => { s.SetNonce(address, nonce); return ""; });
                        break;
                    }
            }
        }

        expected.Commit(spec, NullStateTracer.Instance, commitRoots: true);
        actual.Commit(spec, NullStateTracer.Instance, commitRoots: true);
        AssertSameRoot();

        // After the tree is committed and the block's changes are dropped, every read comes from the committed tree.
        log.Add("commit tree, reset");
        expected.CommitTree(1);
        actual.CommitTree(1);
        expected.Reset();
        actual.Reset();
        foreach (Address address in Addresses)
        {
            Compare(s => Describe(s, address));
            Compare(s => { s.Get(new StorageCell(address, 1), out UInt256 value); return value.ToString(); });
        }

        void AssertSameRoot()
        {
            expected.RecalculateStateRoot();
            actual.RecalculateStateRoot();
            Assert.That(actual.StateRoot, Is.EqualTo(expected.StateRoot), Trace);
        }

        void LiftRevertedClear(Address address)
        {
            if (clearReverted.TryGetValue(address, out int revertedIn) && revertedIn == transaction) clearReverted.Remove(address);
        }

        void Compare(Func<IWorldState, string> operation) =>
            Assert.That(Apply(actual, operation), Is.EqualTo(Apply(expected, operation)), Trace);

        string Trace() => string.Join(Environment.NewLine, log);
    }

    /// <remarks>
    /// EIP-161 removes an account whose storage is still only in the uncommitted trie, and a CREATE lands on it. The
    /// cleared storage must not come back from that trie.
    /// </remarks>
    [Test]
    public void Cleared_storage_of_a_removed_and_recreated_account_reads_zero([Values] bool guest)
    {
        Address address = TestItem.Addresses[100];
        StorageCell cell = new(address, 1);
        (IWorldState state, BlockHeader baseBlock) = CreatePreState(guest, builder =>
        {
            builder.CreateAccount(address, 1);
            builder.Set(cell, 7);
        });
        using IDisposable scope = state.BeginScope(baseBlock);

        state.SubtractFromBalance(address, 1, Cancun.Instance);
        state.Commit(Cancun.Instance, NullStateTracer.Instance, commitRoots: false);
        state.CreateAccount(address, 0, 1);
        state.ClearStorage(address);

        state.Get(cell, out UInt256 value);
        Assert.That(value, Is.EqualTo(UInt256.Zero));
    }

    /// <remarks>
    /// A CREATE2 whose init code reverts and a retry at the same address clear the storage twice in one transaction,
    /// with a revert between, and the retry then reads the slot's original.
    /// </remarks>
    [Test]
    public void Clear_after_a_reverted_clear_matches_world_state()
    {
        Address address = TestItem.Addresses[100];
        StorageCell cell = new(address, 1);
        (string expected, string actual) = RunOnBoth(builder =>
        {
            builder.CreateAccount(address, 1, 1);
            builder.Set(cell, 7);
        }, state =>
        {
            Snapshot snapshot = state.TakeSnapshot();
            state.ClearStorage(address);
            state.Set(cell, 5);
            state.Restore(snapshot);
            state.ClearStorage(address);
            state.Get(cell, out UInt256 current);
            state.GetOriginal(cell, out UInt256 original);
            return $"{current} {original}";
        });

        Assert.That(actual, Is.EqualTo(expected));
    }

    /// <remarks>
    /// A code that the witness leaves out is served only while a creation that deployed it stands. A repeated deployment
    /// that reverts does not remove the code of the first one.
    /// </remarks>
    [Test]
    public void Reverted_creation_takes_its_code_away([Values] bool guest)
    {
        MemDb codeDb = new();
        (IWorldState state, BlockHeader baseBlock) = CreatePreState(guest, codeDb: codeDb);
        Hash256 codeHash = Keccak.Compute(Codes[0]);
        codeDb.Remove(codeHash.Bytes);
        using IDisposable scope = state.BeginScope(baseBlock);

        Snapshot beforeCreation = state.TakeSnapshot();
        state.CreateAccount(Addresses[3], 0, 1);
        state.InsertCode(Addresses[3], codeHash, Codes[0], Cancun.Instance);
        Snapshot beforeRepeat = state.TakeSnapshot();
        state.CreateAccount(Addresses[4], 0, 1);
        state.InsertCode(Addresses[4], codeHash, Codes[0], Cancun.Instance);
        state.Restore(beforeRepeat);
        byte[] afterRepeat = state.GetCode(Addresses[0]).ToArray();
        state.Restore(beforeCreation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterRepeat, Is.EqualTo(Codes[0]));
            Assert.That(() => state.GetCode(Addresses[0]), Throws.InvalidOperationException);
        }
    }

    [Test]
    public void Code_missing_from_the_witness_throws()
    {
        MemDb codeDb = new();
        (IWorldState state, BlockHeader baseBlock) = CreatePreState(guest: true, codeDb: codeDb);
        ValueHash256 codeHash = Keccak.Compute(Codes[0]).ValueHash256;
        codeDb.Remove(codeHash.Bytes);
        using IDisposable scope = state.BeginScope(baseBlock);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => state.RecordBytecodeAccess(Addresses[0]), Throws.InvalidOperationException);
            Assert.That(() => state.GetCode(Addresses[0]), Throws.InvalidOperationException);
            Assert.That(() => state.GetCode(in codeHash), Throws.InvalidOperationException);
        }
    }

    /// <remarks>
    /// Goes past the initial sizes of the account, slot, transient and journal tables, so each grows and rehashes. A
    /// storage destruction, a tree commit and a reset follow.
    /// </remarks>
    [Test]
    public void Matches_world_state_past_the_initial_table_sizes()
    {
        const int accounts = 1_200;
        const int slots = 9_000;
        (string expected, string actual) = RunOnBoth(null, state =>
        {
            List<string> reads = [];
            for (int i = 0; i < accounts; i++)
            {
                state.CreateAccount(AccountAt(i), 1, 1);
            }

            Snapshot snapshot = state.TakeSnapshot();
            for (int i = 0; i < slots; i++)
            {
                StorageCell cell = new(AccountAt(i % accounts), (UInt256)i);
                state.Set(cell, (UInt256)(i + 1));
                state.SetTransientState(cell, (UInt256)(i + 2));
            }

            state.Restore(snapshot);
            for (int i = 0; i < slots; i++)
            {
                state.Set(new StorageCell(AccountAt(i % accounts), (UInt256)i), (UInt256)(i + 3));
            }

            state.Commit(Cancun.Instance, NullStateTracer.Instance, commitRoots: true);
            state.MarkStorageDestroyed(AccountAt(0));
            state.Commit(Cancun.Instance, NullStateTracer.Instance, commitRoots: true);
            state.RecalculateStateRoot();
            reads.Add(state.StateRoot.ToString());
            state.CommitTree(1);
            state.Reset();
            for (int i = 0; i < slots; i += 97)
            {
                state.Get(new StorageCell(AccountAt(i % accounts), (UInt256)i), out UInt256 value);
                reads.Add(value.ToString());
            }

            return string.Join(",", reads);
        });

        Assert.That(actual, Is.EqualTo(expected));

        static Address AccountAt(int i) => Address.FromNumber((UInt256)(i + 1_000));
    }

    /// <remarks>
    /// An EIP-8360 <c>TCREATE</c> account's code is readable by its hash until the transaction ends, and is never
    /// written to the code database.
    /// </remarks>
    [Test]
    public void Transient_code_matches_world_state()
    {
        Address address = TestItem.Addresses[100];
        byte[] code = Codes[2];
        Hash256 codeHash = Keccak.Compute(code);
        (string expected, string actual) = RunOnBoth(null, state =>
        {
            state.CreateAccount(address, 0, 1);
            state.InsertTransientCode(address, codeHash, code, Cancun.Instance);
            string during = $"{state.GetCodeHash(address)} {state.GetCode(address).ToArray().ToHexString()} {state.GetCode(codeHash.ValueHash256).ToArray().ToHexString()}";
            state.DeleteAccount(address);
            state.DiscardTransientCode();
            return $"{during} {Apply(state, s => s.GetCode(codeHash.ValueHash256).ToArray().ToHexString())}";
        });

        Assert.That(actual, Is.EqualTo(expected));
    }

    /// <summary>Runs <paramref name="steps"/> on a <see cref="WorldState"/> and on a <see cref="GuestWorldState"/> over the same pre-state.</summary>
    private static (string Expected, string Actual) RunOnBoth(Action<WorldState> addState, Func<IWorldState, string> steps)
    {
        return (RunOn(guest: false), RunOn(guest: true));

        string RunOn(bool guest)
        {
            (IWorldState state, BlockHeader baseBlock) = CreatePreState(guest, addState);
            using IDisposable scope = state.BeginScope(baseBlock);
            return Apply(state, steps);
        }
    }

    [Test]
    public void Next_scope_does_not_see_the_changes_of_an_ended_one()
    {
        (IWorldState state, BlockHeader baseBlock) = CreatePreState(guest: true);
        StorageCell cell = new(Addresses[0], 1);
        using (state.BeginScope(baseBlock))
        {
            state.AddToBalance(Addresses[0], 5, Cancun.Instance);
            state.Set(cell, 9);
            state.Commit(Cancun.Instance, NullStateTracer.Instance, commitRoots: false);
        }

        using IDisposable scope = state.BeginScope(baseBlock);
        state.Get(cell, out UInt256 value);
        Assert.That((state.GetBalance(Addresses[0]), value), Is.EqualTo(((UInt256)0, (UInt256)2)));
    }

    private static string Apply(IWorldState state, Func<IWorldState, string> operation)
    {
        try
        {
            return operation(state);
        }
        catch (Exception e)
        {
            return $"throws {e.GetType().Name}";
        }
    }

    private static string Describe(IWorldState state, Address address) =>
        $"{state.AccountExists(address)} {state.IsDeadAccount(address)} {state.IsContract(address)} {state.GetNonce(address)} " +
        $"{state.GetBalance(address)} {state.GetCodeHash(address)}";

    /// <remarks>A, B and C exist, A and B with code and storage; D, E and F do not.</remarks>
    private static (IWorldState State, BlockHeader Base) CreatePreState(bool guest, Action<WorldState> addState = null, MemDb codeDb = null)
    {
        TrieStoreScopeProvider scopeProvider = new(
            new TestRawTrieStore(new NodeStorage(new MemDb())), codeDb ?? new MemDb(), UnavailableStateHeaderProvider.Instance, LimboLogs.Instance);
        WorldState builder = new(scopeProvider, LimboLogs.Instance);

        BlockHeader baseBlock;
        using (builder.BeginScope(IWorldState.PreGenesis))
        {
            for (int i = 0; i < 3; i++)
            {
                builder.CreateAccount(Addresses[i], (UInt256)(i * 3), (ulong)(i % 2));
            }

            for (int i = 0; i < 2; i++)
            {
                builder.InsertCode(Addresses[i], Keccak.Compute(Codes[i]), Codes[i], Cancun.Instance);
                for (int slot = 0; slot < 4; slot++)
                {
                    builder.Set(new StorageCell(Addresses[i], (UInt256)slot), (UInt256)(slot + i + 1));
                }
            }

            addState?.Invoke(builder);
            builder.Commit(Cancun.Instance);
            builder.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithNumber(0).WithStateRoot(builder.StateRoot).TestObject;
        }

        return (guest ? new GuestWorldState(scopeProvider) : builder, baseBlock);
    }
}
