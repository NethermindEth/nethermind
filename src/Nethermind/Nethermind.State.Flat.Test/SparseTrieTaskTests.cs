// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SparseTrieTaskTests
{
    [Test]
    public async Task FinalAccountState_MatchesPatricia()
    {
        TestState state = BuildState(accountCount: 20);
        Address address = state.Addresses[3];
        Account finalAccount = new(100, (UInt256)123_456);
        Hash256 expectedRoot = ApplyAccount(state.StateTree, address, finalAccount);

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, state.Reader);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(address, finalAccount)],
            []));

        SparseTrieBlockResult result = await block.FinishAsync(
            new SparseTrieFinalState([], [new SparseTrieFinalAccount(address, finalAccount)]));
        await block.PrepareCommitAsync();
        await block.AcceptAsync();

        Assert.That(result.StateRoot, Is.EqualTo(expectedRoot));
    }

    [Test]
    public async Task ProvisionalAccountReturningToOriginal_IsReconciled()
    {
        TestState state = BuildState(accountCount: 20);
        Address address = state.Addresses[3];
        Account originalAccount = new(3, (UInt256)1_003);
        Account provisionalAccount = new(99, (UInt256)99_000);

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, state.Reader);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(address, provisionalAccount)],
            []));

        SparseTrieBlockResult result = await block.FinishAsync(
            new SparseTrieFinalState([], [new SparseTrieFinalAccount(address, originalAccount)]));
        await block.PrepareCommitAsync();
        await block.AcceptAsync();

        Assert.That(result.StateRoot, Is.EqualTo(state.ParentRoot));
    }

    [Test]
    public async Task SequentialAcceptedBlocks_ReuseOnlyExactParentAnchor()
    {
        TestState state = BuildState(accountCount: 20);
        Address firstAddress = state.Addresses[0];
        Address secondAddress = state.Addresses[7];
        Account firstAccount = new(10, (UInt256)10_000);
        Account secondAccount = new(20, (UInt256)20_000);

        Hash256 firstRoot = ApplyAccount(state.StateTree, firstAddress, firstAccount);
        Hash256 secondRoot = ApplyAccount(state.StateTree, secondAddress, secondAccount);

        await using SparseTrieWorker worker = CreateWorker();
        await using (SparseTrieBlockHandle first = worker.BeginBlock(state.ParentRoot, state.Reader))
        {
            first.EnqueueDelta(new SparseTriePhaseDelta(
                [new SparseTrieAccountDelta(firstAddress, firstAccount)],
                []));
            SparseTrieBlockResult result = await first.FinishAsync(
                new SparseTrieFinalState([], [new SparseTrieFinalAccount(firstAddress, firstAccount)]));
            Assert.That(result.StateRoot, Is.EqualTo(firstRoot));
            await first.PrepareCommitAsync();
            await first.AcceptAsync();
        }

        await using (SparseTrieBlockHandle second = worker.BeginBlock(firstRoot, state.Reader))
        {
            second.EnqueueDelta(new SparseTriePhaseDelta(
                [new SparseTrieAccountDelta(secondAddress, secondAccount)],
                []));
            SparseTrieBlockResult result = await second.FinishAsync(
                new SparseTrieFinalState([], [new SparseTrieFinalAccount(secondAddress, secondAccount)]));
            Assert.That(result.StateRoot, Is.EqualTo(secondRoot));
            await second.PrepareCommitAsync();
            await second.AcceptAsync();
        }

        Address forkAddress = state.Addresses[11];
        Account forkAccount = new(30, (UInt256)30_000);
        PatriciaTree forkTree = new(state.Store.GetTrieStore(null), LimboLogs.Instance)
        {
            RootHash = state.ParentRoot,
        };
        Hash256 forkRoot = ApplyAccount(forkTree, forkAddress, forkAccount);

        await using SparseTrieBlockHandle fork = worker.BeginBlock(state.ParentRoot, state.Reader);
        fork.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(forkAddress, forkAccount)],
            []));
        SparseTrieBlockResult forkResult = await fork.FinishAsync(
            new SparseTrieFinalState([], [new SparseTrieFinalAccount(forkAddress, forkAccount)]));
        await fork.PrepareCommitAsync();
        await fork.AcceptAsync();

        Assert.That(forkResult.StateRoot, Is.EqualTo(forkRoot));
    }

    [Test]
    public async Task ProvisionalStorage_IsReconciledWithExactFinalValues()
    {
        StorageTestState state = BuildStorageState();
        byte[] provisionalValue = [0x99];
        byte[] finalChangedValue = [0x33];

        state.StorageTree.Set(state.ChangedSlot, finalChangedValue);
        state.StorageTree.UpdateRootHash();
        state.StorageTree.Commit();
        Hash256 expectedStorageRoot = state.StorageTree.RootHash;
        Account expectedAccount = state.ParentAccount.WithChangedStorageRoot(expectedStorageRoot);
        Hash256 expectedStateRoot = ApplyAccount(state.State.StateTree, state.Address, expectedAccount);

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.State.ParentRoot, state.State.Reader);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [],
            [new SparseTrieStorageDelta(
                state.Address,
                state.ParentStorageRoot,
                state.UnchangedSlot,
                provisionalValue)]));

        SparseTrieBlockResult result = await block.FinishAsync(new SparseTrieFinalState(
            [new SparseTrieFinalStorageBatch(
                state.Address,
                state.ParentStorageRoot,
                Clear: false,
                [
                    new SparseTrieFinalSlot(state.UnchangedSlot, state.UnchangedValue),
                    new SparseTrieFinalSlot(state.ChangedSlot, finalChangedValue),
                ])],
            [new SparseTrieFinalAccount(state.Address, state.ParentAccount)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(expectedStateRoot));
            Assert.That(result.StorageRoots[state.Address], Is.EqualTo(expectedStorageRoot));
        }

        await block.PrepareCommitAsync();
        await block.AcceptAsync();
    }

    [Test]
    public async Task SequentialAcceptedBlocks_RetainStorageTrie()
    {
        StorageTestState state = BuildStorageState();
        byte[] firstValue = [0x33];
        state.StorageTree.Set(state.ChangedSlot, firstValue);
        state.StorageTree.UpdateRootHash();
        state.StorageTree.Commit();
        Hash256 firstStorageRoot = state.StorageTree.RootHash;
        Account firstAccount = state.ParentAccount.WithChangedStorageRoot(firstStorageRoot);
        Hash256 firstStateRoot = ApplyAccount(state.State.StateTree, state.Address, firstAccount);

        await using SparseTrieWorker worker = CreateWorker();
        await using (SparseTrieBlockHandle first = worker.BeginBlock(state.State.ParentRoot, state.State.Reader))
        {
            first.EnqueueDelta(new SparseTriePhaseDelta(
                [],
                [new SparseTrieStorageDelta(
                    state.Address,
                    state.ParentStorageRoot,
                    state.ChangedSlot,
                    firstValue)]));
            SparseTrieBlockResult result = await first.FinishAsync(new SparseTrieFinalState(
                [new SparseTrieFinalStorageBatch(
                    state.Address,
                    state.ParentStorageRoot,
                    Clear: false,
                    [new SparseTrieFinalSlot(state.ChangedSlot, firstValue)])],
                [new SparseTrieFinalAccount(state.Address, state.ParentAccount)]));
            Assert.That(result.StateRoot, Is.EqualTo(firstStateRoot));
            await first.PrepareCommitAsync();
            await first.AcceptAsync();
        }

        byte[] secondValue = [0x44];
        state.StorageTree.Set(state.UnchangedSlot, secondValue);
        state.StorageTree.UpdateRootHash();
        state.StorageTree.Commit();
        Hash256 secondStorageRoot = state.StorageTree.RootHash;
        Account secondAccount = firstAccount.WithChangedStorageRoot(secondStorageRoot);
        Hash256 secondStateRoot = ApplyAccount(state.State.StateTree, state.Address, secondAccount);

        await using SparseTrieBlockHandle second = worker.BeginBlock(firstStateRoot, state.State.Reader);
        second.EnqueueDelta(new SparseTriePhaseDelta(
            [],
            [new SparseTrieStorageDelta(
                state.Address,
                firstStorageRoot,
                state.UnchangedSlot,
                secondValue)]));
        SparseTrieBlockResult secondResult = await second.FinishAsync(new SparseTrieFinalState(
            [new SparseTrieFinalStorageBatch(
                state.Address,
                firstStorageRoot,
                Clear: false,
                [new SparseTrieFinalSlot(state.UnchangedSlot, secondValue)])],
            [new SparseTrieFinalAccount(state.Address, firstAccount)]));
        await second.PrepareCommitAsync();
        await second.AcceptAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondResult.StorageRoots[state.Address], Is.EqualTo(secondStorageRoot));
            Assert.That(secondResult.StateRoot, Is.EqualTo(secondStateRoot));
        }
    }

    [Test]
    public async Task DeletedAccountWithoutStorageBatch_CanBeRecreatedFromEmptyStorage()
    {
        StorageTestState state = BuildStorageState();
        state.State.StateTree.Set(state.Address.ToAccountPath.Bytes, []);
        state.State.StateTree.UpdateRootHash();
        state.State.StateTree.Commit();
        Hash256 deletedStateRoot = state.State.StateTree.RootHash;

        await using SparseTrieWorker worker = CreateWorker();
        await using (SparseTrieBlockHandle deleted = worker.BeginBlock(
            state.State.ParentRoot,
            state.State.Reader))
        {
            deleted.EnqueueDelta(new SparseTriePhaseDelta(
                [new SparseTrieAccountDelta(state.Address, null)],
                []));
            SparseTrieBlockResult result = await deleted.FinishAsync(
                new SparseTrieFinalState([], [new SparseTrieFinalAccount(state.Address, null)]));
            Assert.That(result.StateRoot, Is.EqualTo(deletedStateRoot));
            await deleted.PrepareCommitAsync();
            await deleted.AcceptAsync();
        }

        UInt256 recreatedSlot = 9;
        byte[] recreatedValue = [0x55];
        Hash256 addressHash = state.Address.ToAccountPath.ToCommitment();
        StorageTree recreatedStorage = new(
            state.State.Store.GetTrieStore(addressHash),
            LimboLogs.Instance);
        recreatedStorage.Set(recreatedSlot, recreatedValue);
        recreatedStorage.UpdateRootHash();
        recreatedStorage.Commit();
        Hash256 recreatedStorageRoot = recreatedStorage.RootHash;
        Account recreatedAccount = new(2, (UInt256)6_000);
        Hash256 recreatedStateRoot = ApplyAccount(
            state.State.StateTree,
            state.Address,
            recreatedAccount.WithChangedStorageRoot(recreatedStorageRoot));

        await using SparseTrieBlockHandle recreated = worker.BeginBlock(
            deletedStateRoot,
            state.State.Reader);
        recreated.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(state.Address, recreatedAccount)],
            [new SparseTrieStorageDelta(
                state.Address,
                Keccak.EmptyTreeHash,
                recreatedSlot,
                recreatedValue)]));
        SparseTrieBlockResult recreatedResult = await recreated.FinishAsync(
            new SparseTrieFinalState(
                [new SparseTrieFinalStorageBatch(
                    state.Address,
                    Keccak.EmptyTreeHash,
                    Clear: false,
                    [new SparseTrieFinalSlot(recreatedSlot, recreatedValue)])],
                [new SparseTrieFinalAccount(state.Address, recreatedAccount)]));
        await recreated.PrepareCommitAsync();
        await recreated.AcceptAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recreatedResult.StorageRoots[state.Address], Is.EqualTo(recreatedStorageRoot));
            Assert.That(recreatedResult.StateRoot, Is.EqualTo(recreatedStateRoot));
        }
    }

    [Test]
    public async Task IncompleteFinalStorage_PoisonsResultAndWorkerCanRecoverAfterAbort()
    {
        StorageTestState state = BuildStorageState();
        await using SparseTrieWorker worker = CreateWorker();

        await using (SparseTrieBlockHandle poisoned = worker.BeginBlock(state.State.ParentRoot, state.State.Reader))
        {
            poisoned.EnqueueDelta(new SparseTriePhaseDelta(
                [],
                [new SparseTrieStorageDelta(
                    state.Address,
                    state.ParentStorageRoot,
                    state.UnchangedSlot,
                    [0x42])]));

            Func<Task> finish = async () => await poisoned.FinishAsync(
                new SparseTrieFinalState([], [new SparseTrieFinalAccount(state.Address, state.ParentAccount)]));
            Assert.ThrowsAsync<InvalidOperationException>(finish);
            await poisoned.AbortAsync();
        }

        await using SparseTrieBlockHandle recovered = worker.BeginBlock(state.State.ParentRoot, state.State.Reader);
        SparseTrieBlockResult recoveredResult = await recovered.FinishAsync(new SparseTrieFinalState([], []));
        await recovered.PrepareCommitAsync();
        await recovered.AcceptAsync();

        Assert.That(recoveredResult.StateRoot, Is.EqualTo(state.State.ParentRoot));
    }

    private static SparseTrieWorker CreateWorker() =>
        new(LimboLogs.Instance.GetClassLogger<SparseTrieTaskTests>(), CancellationToken.None);

    private static TestState BuildState(int accountCount)
    {
        MemDb db = new();
        RawTrieStore store = new(db);
        PatriciaTree stateTree = new(store.GetTrieStore(null), LimboLogs.Instance);
        Address[] addresses = new Address[accountCount];

        for (int i = 0; i < accountCount; i++)
        {
            UInt256 addressNumber = (UInt256)(i + 1);
            Address address = Address.FromNumber(in addressNumber);
            addresses[i] = address;
            Account account = new((ulong)i, (UInt256)(1_000 + i));
            stateTree.Set(address.ToAccountPath.Bytes, AccountDecoder.Instance.Encode(account).Bytes);
        }

        stateTree.UpdateRootHash();
        stateTree.Commit();
        return new TestState(
            store,
            stateTree,
            addresses,
            stateTree.RootHash,
            new HalfPathTrieNodeReader(new NodeStorage(db)));
    }

    private static StorageTestState BuildStorageState()
    {
        TestState state = BuildState(accountCount: 8);
        Address address = state.Addresses[4];
        Hash256 addressHash = address.ToAccountPath.ToCommitment();
        StorageTree storageTree = new(state.Store.GetTrieStore(addressHash), LimboLogs.Instance);
        UInt256 unchangedSlot = 1;
        UInt256 changedSlot = 2;
        byte[] unchangedValue = [0x11];
        storageTree.Set(unchangedSlot, unchangedValue);
        storageTree.Set(changedSlot, [0x22]);
        storageTree.UpdateRootHash();
        storageTree.Commit();

        Hash256 parentStorageRoot = storageTree.RootHash;
        Account parentAccount = new(1, (UInt256)5_000, parentStorageRoot, Keccak.OfAnEmptyString);
        state.StateTree.Set(
            address.ToAccountPath.Bytes,
            AccountDecoder.Instance.Encode(parentAccount).Bytes);
        state.StateTree.UpdateRootHash();
        state.StateTree.Commit();

        state = state with { ParentRoot = state.StateTree.RootHash };
        return new StorageTestState(
            state,
            address,
            parentAccount,
            storageTree,
            parentStorageRoot,
            unchangedSlot,
            changedSlot,
            unchangedValue);
    }

    private static Hash256 ApplyAccount(PatriciaTree tree, Address address, Account account)
    {
        tree.Set(address.ToAccountPath.Bytes, AccountDecoder.Instance.Encode(account).Bytes);
        tree.UpdateRootHash();
        tree.Commit();
        return tree.RootHash;
    }

    private sealed record TestState(
        RawTrieStore Store,
        PatriciaTree StateTree,
        Address[] Addresses,
        Hash256 ParentRoot,
        HalfPathTrieNodeReader Reader);

    private sealed record StorageTestState(
        TestState State,
        Address Address,
        Account ParentAccount,
        StorageTree StorageTree,
        Hash256 ParentStorageRoot,
        UInt256 UnchangedSlot,
        UInt256 ChangedSlot,
        byte[] UnchangedValue);
}
