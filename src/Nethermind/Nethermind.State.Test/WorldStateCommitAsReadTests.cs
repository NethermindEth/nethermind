// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

/// <summary>
/// <see cref="WorldState.ChangesCommitAsReadSince"/> tells the changes of a transaction that a commit writes as they read,
/// so the transactions after them read the same state, and one commit leaves the state of committing after each.
/// </summary>
[TestFixture(false)]
[TestFixture(true)]
[Parallelizable(ParallelScope.All)]
public class WorldStateCommitAsReadTests(bool useFlat)
{
    private static readonly IReleaseSpec Spec = Cancun.Instance;
    private static readonly Address Funded = TestItem.AddressA;
    private static readonly Address OtherFunded = TestItem.AddressB;
    private static readonly Address Empty = TestItem.AddressC;
    // Empty, with storage only genesis could leave it (EIP-161).
    private static readonly Address EmptyWithStorage = TestItem.AddressD;
    private static readonly Address Contract = TestItem.AddressE;
    private static readonly Address Missing = TestItem.AddressF;
    private static readonly Address OtherMissing = TestItem.Addresses[0x4e];
    private static readonly Address[] Accounts = [Funded, OtherFunded, Empty, EmptyWithStorage, Contract, Missing, OtherMissing];
    private static readonly byte[] Code = [0x60, 0x00, 0x60, 0x00];

    public enum Change
    {
        Read,
        Credit,
        Debit,
        NonceIncrement,
        SlotWrite,
        SlotWrittenBack,
        MissingAccountFunded,
        EmptyAccountTouched,
        EmptyAccountFunded,
        FundedAccountTouched,
        MissingAccountCreatedEmpty,
        AccountEmptied,
        AccountDeleted,
        AccountRecreated,
        StorageCleared,
        StorageDestroyed,
        CodeInserted,
        TransientWritten
    }

    [Test]
    public void Tells_the_changes_a_commit_writes_as_they_read([Values] Change change)
    {
        using Context ctx = new(useFlat);
        WorldState state = ctx.State;
        using IDisposable scope = state.BeginScope(ctx.BaseBlock);
        // A transaction before it, left uncommitted.
        state.AddToBalance(OtherFunded, 1, Spec);
        Snapshot snapshot = state.TakeSnapshot();

        switch (change)
        {
            case Change.Read:
                state.GetBalance(Funded);
                state.AccountExists(Missing);
                state.Get(new StorageCell(Contract, 1), out _);
                break;
            case Change.Credit:
                state.AddToBalance(Funded, 1, Spec);
                break;
            case Change.Debit:
                state.SubtractFromBalance(Funded, 1, Spec);
                break;
            case Change.NonceIncrement:
                state.IncrementNonce(Funded, 1, out _);
                break;
            case Change.SlotWrite:
                state.Set(new StorageCell(Contract, 1), 2);
                break;
            case Change.SlotWrittenBack:
                state.Set(new StorageCell(Contract, 1), 2);
                state.Set(new StorageCell(Contract, 1), 1);
                break;
            case Change.MissingAccountFunded:
                state.AddToBalanceAndCreateIfNotExists(Missing, 1, Spec);
                break;
            case Change.EmptyAccountTouched:
                state.AddToBalance(Empty, 0, Spec);
                break;
            case Change.EmptyAccountFunded:
                state.AddToBalance(Empty, 1, Spec);
                break;
            case Change.FundedAccountTouched:
                state.AddToBalance(Funded, 0, Spec);
                break;
            case Change.MissingAccountCreatedEmpty:
                state.AddToBalanceAndCreateIfNotExists(Missing, 0, Spec);
                break;
            case Change.AccountEmptied:
                state.SubtractFromBalance(OtherFunded, state.GetBalance(OtherFunded), Spec);
                break;
            case Change.AccountDeleted:
                state.DeleteAccount(Funded);
                break;
            case Change.AccountRecreated:
                state.DeleteAccount(Funded);
                state.CreateAccount(Funded, 1);
                break;
            case Change.StorageCleared:
                state.ClearStorage(Contract);
                break;
            case Change.StorageDestroyed:
                state.MarkStorageDestroyed(Contract);
                break;
            case Change.CodeInserted:
                state.InsertCode(Funded, new byte[] { 0x5F }, Spec);
                break;
            default:
                state.SetTransientState(new StorageCell(Contract, 1), 1);
                break;
        }

        bool commitsAsRead = change is Change.Read or Change.Credit or Change.Debit or Change.NonceIncrement or Change.SlotWrite
            or Change.SlotWrittenBack or Change.MissingAccountFunded or Change.EmptyAccountFunded or Change.FundedAccountTouched;
        Assert.That(state.ChangesCommitAsReadSince(in snapshot), Is.EqualTo(commitsAsRead));
    }

    [Test]
    public void Only_the_changes_since_the_snapshot_are_told([Values] bool earlierTouched)
    {
        using Context ctx = new(useFlat);
        WorldState state = ctx.State;
        using IDisposable scope = state.BeginScope(ctx.BaseBlock);
        Snapshot start = state.TakeSnapshot();
        if (earlierTouched) state.AddToBalance(Empty, 0, Spec);
        else state.AddToBalance(Empty, 1, Spec);
        Snapshot snapshot = state.TakeSnapshot();
        state.AddToBalance(Funded, 1, Spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.ChangesCommitAsReadSince(in snapshot), Is.True);
            Assert.That(state.ChangesCommitAsReadSince(in start), Is.EqualTo(!earlierTouched));
        }
    }

    /// <summary>
    /// Random transactions of balance, nonce and slot changes, committed after each, and committed only when the
    /// changes of one do not commit as they read: every transaction reads the same and the blocks end in the same root.
    /// </summary>
    [Test]
    public void Changes_that_commit_as_read_commit_together_into_the_state_of_committing_each()
    {
        using Context ctx = new(useFlat);
        WorldState state = ctx.State;
        int shared = 0;
        for (int seed = 0; seed < 300; seed++)
        {
            (Hash256 root, List<string> reads) eachCommitted = Run(new Random(seed), share: false, out _);
            (Hash256 root, List<string> reads) sharing = Run(new Random(seed), share: true, out int sharedInBlock);
            shared += sharedInBlock;
            Assert.That(sharing.reads, Is.EqualTo(eachCommitted.reads), $"reads of seed {seed}");
            Assert.That(sharing.root, Is.EqualTo(eachCommitted.root), $"root of seed {seed}");
        }

        Assert.That(shared, Is.GreaterThan(1000), "precondition: commits were shared");

        (Hash256, List<string>) Run(Random rng, bool share, out int sharedCommits)
        {
            sharedCommits = 0;
            List<string> reads = [];
            using IDisposable scope = state.BeginScope(ctx.BaseBlock);
            int transactions = rng.Next(1, 24);
            for (int tx = 0; tx < transactions; tx++)
            {
                Snapshot snapshot = state.TakeSnapshot();
                int changes = rng.Next(1, 5);
                for (int i = 0; i < changes; i++) RandomChange(state, rng);
                if (!share || !state.ChangesCommitAsReadSince(in snapshot)) state.Commit(Spec, NullStateTracer.Instance, commitRoots: false);
                else sharedCommits++;
                reads.Add(Describe(state));
            }

            state.Commit(Spec);
            state.RecalculateStateRoot();
            return (state.StateRoot, reads);
        }
    }

    private static void RandomChange(IWorldState state, Random rng)
    {
        Address address = Accounts[rng.Next(Accounts.Length)];
        switch (rng.Next(6))
        {
            case 0:
                // Zero credits touch: an empty account is removed (EIP-161), a missing one created empty.
                state.AddToBalanceAndCreateIfNotExists(address, (UInt256)rng.Next(0, 3), Spec);
                break;
            case 1:
                UInt256 debit = (UInt256)rng.Next(0, 3);
                if (state.GetBalance(address) >= debit && state.AccountExists(address)) state.SubtractFromBalance(address, debit, Spec);
                break;
            case 2:
                if (state.AccountExists(address)) state.IncrementNonce(address, 1, out _);
                break;
            case 3:
                state.Get(new StorageCell(address, (UInt256)rng.Next(0, 3)), out _);
                break;
            default:
                Address contract = rng.Next(2) == 0 ? Contract : EmptyWithStorage;
                if (state.AccountExists(contract)) state.Set(new StorageCell(contract, (UInt256)rng.Next(0, 3)), (UInt256)rng.Next(0, 3));
                break;
        }
    }

    private static string Describe(IWorldState state)
    {
        List<string> parts = [];
        foreach (Address address in Accounts)
        {
            parts.Add($"{address}: {state.AccountExists(address)} {state.IsDeadAccount(address)} {state.GetBalance(address)} {state.GetNonce(address)}");
        }

        foreach (Address contract in new[] { Contract, EmptyWithStorage })
        {
            for (int i = 0; i < 3; i++)
            {
                state.Get(new StorageCell(contract, (UInt256)i), out UInt256 value);
                parts.Add($"{contract}[{i}]: {value}");
            }
        }

        return string.Join("; ", parts);
    }

    private sealed class Context : IDisposable
    {
        private readonly IContainer? _container;

        public WorldState State { get; }

        public BlockHeader BaseBlock { get; }

        public Context(bool useFlat)
        {
            if (useFlat)
            {
                (IWorldStateScopeProvider scopeProvider, IContainer container) = TestWorldStateFactory.CreateFlatScopeProvider(UnavailableStateHeaderProvider.Instance);
                _container = container;
                State = new WorldState(scopeProvider, LimboLogs.Instance);
            }
            else
            {
                State = (WorldState)TestWorldStateFactory.CreateForTest();
            }

            using (State.BeginScope(IWorldState.PreGenesis))
            {
                State.CreateAccount(Funded, 100);
                State.CreateAccount(OtherFunded, 100);
                State.CreateAccount(Empty, 0);
                State.CreateAccount(EmptyWithStorage, 0);
                State.Set(new StorageCell(EmptyWithStorage, 1), 0x4e4d);
                State.CreateAccount(Contract, 0);
                State.InsertCode(Contract, Code, Spec);
                State.Set(new StorageCell(Contract, 1), 1);
                State.Set(new StorageCell(Contract, 2), 2);
                // As genesis, which keeps empty accounts.
                State.Commit(Spec, NullStateTracer.Instance, isGenesis: true);
                State.CommitTree(0);
                BaseBlock = Build.A.BlockHeader.WithStateRoot(State.StateRoot).TestObject;
            }
        }

        public void Dispose() => _container?.Dispose();
    }
}
