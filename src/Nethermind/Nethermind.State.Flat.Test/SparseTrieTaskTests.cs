// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
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
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SparseTrieTaskTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task FinalAccountState_MatchesPatricia(bool preReveal)
    {
        TestState state = BuildState(accountCount: 20);
        Address address = state.Addresses[3];
        Account finalAccount = new(100, (UInt256)123_456);
        Hash256 expectedRoot = ApplyAccount(state.StateTree, address, finalAccount);

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, state.Reader);
        if (preReveal)
            Assert.That(block.TryEnqueueAccountTouch(address.ToAccountPath), Is.True);
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
    public async Task RepeatedProvisionalStorageWrites_AreReconciledWithExactFinalValues()
    {
        StorageTestState state = BuildStorageState();
        byte[] finalUnchangedValue = state.UnchangedValue;
        byte[] finalDeletedValue = [0];

        state.StorageTree.Set(state.ChangedSlot, finalDeletedValue);
        state.StorageTree.UpdateRootHash();
        state.StorageTree.Commit();
        Hash256 expectedStorageRoot = state.StorageTree.RootHash;
        Account expectedAccount = state.ParentAccount.WithChangedStorageRoot(expectedStorageRoot);
        Hash256 expectedStateRoot = ApplyAccount(state.State.StateTree, state.Address, expectedAccount);

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.State.ParentRoot, state.State.Reader);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [],
            [
                new SparseTrieStorageDelta(
                    state.Address,
                    state.ParentStorageRoot,
                    state.UnchangedSlot,
                    [0x99]),
                new SparseTrieStorageDelta(
                    state.Address,
                    state.ParentStorageRoot,
                    state.ChangedSlot,
                    [0x77]),
            ]));
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [],
            [
                new SparseTrieStorageDelta(
                    state.Address,
                    state.ParentStorageRoot,
                    state.UnchangedSlot,
                    [0x88]),
                new SparseTrieStorageDelta(
                    state.Address,
                    state.ParentStorageRoot,
                    state.ChangedSlot,
                    [0x66]),
            ]));
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [],
            [
                new SparseTrieStorageDelta(
                    state.Address,
                    state.ParentStorageRoot,
                    state.UnchangedSlot,
                    finalUnchangedValue),
                new SparseTrieStorageDelta(
                    state.Address,
                    state.ParentStorageRoot,
                    state.ChangedSlot,
                    finalDeletedValue),
            ]));

        SparseTrieBlockResult result = await block.FinishAsync(new SparseTrieFinalState(
            [new SparseTrieFinalStorageBatch(
                state.Address,
                state.ParentStorageRoot,
                Clear: false,
                [
                    new SparseTrieFinalSlot(state.UnchangedSlot, finalUnchangedValue, Changed: false),
                    new SparseTrieFinalSlot(state.ChangedSlot, finalDeletedValue),
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

    [TestCase(false)]
    [TestCase(true)]
    public async Task DeltaAndWarmerStorageTouch_ShareSessionDedupe(bool warmerFirst)
    {
        StorageTestState state = BuildStorageState();
        byte[] finalValue = [0x55];
        state.StorageTree.Set(state.ChangedSlot, finalValue);
        state.StorageTree.UpdateRootHash();
        state.StorageTree.Commit();
        Hash256 expectedStorageRoot = state.StorageTree.RootHash;
        Account expectedAccount = state.ParentAccount.WithChangedStorageRoot(expectedStorageRoot);
        Hash256 expectedStateRoot = ApplyAccount(state.State.StateTree, state.Address, expectedAccount);

        using CountingTrieNodeReader reader = new(state.State.Reader);
        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.State.ParentRoot, reader);
        ValueHash256 slotPath = default;
        StorageTree.ComputeKeyWithLookup(state.ChangedSlot, ref slotPath);
        Hash256 accountHash = state.Address.ToAccountPath.ToCommitment();
        SparseTriePhaseDelta delta = new(
            [],
            [new SparseTrieStorageDelta(
                state.Address,
                state.ParentStorageRoot,
                state.ChangedSlot,
                finalValue)]);

        if (warmerFirst)
        {
            Assert.That(
                block.TryEnqueueStorageTouch(accountHash, state.ParentStorageRoot, slotPath),
                Is.True);
        }
        else
        {
            block.EnqueueDelta(delta);
        }

        Assert.That(
            SpinWait.SpinUntil(() => reader.StorageLoads > 0, TimeSpan.FromSeconds(5)),
            Is.True,
            "The worker did not process the first proof hint while idle.");

        if (warmerFirst)
        {
            block.EnqueueDelta(delta);
        }
        else
        {
            Assert.That(
                block.TryEnqueueStorageTouch(accountHash, state.ParentStorageRoot, slotPath),
                Is.True);
        }

        SparseTrieBlockResult result = await block.FinishAsync(new SparseTrieFinalState(
            [new SparseTrieFinalStorageBatch(
                state.Address,
                state.ParentStorageRoot,
                Clear: false,
                [new SparseTrieFinalSlot(state.ChangedSlot, finalValue)])],
            [new SparseTrieFinalAccount(state.Address, state.ParentAccount)]));
        await block.PrepareCommitAsync();
        await block.AcceptAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StorageRoots[state.Address], Is.EqualTo(expectedStorageRoot));
            Assert.That(result.StateRoot, Is.EqualTo(expectedStateRoot));
        }
    }

    [TestCase(PendingFinalStorageChange.Changed)]
    [TestCase(PendingFinalStorageChange.Unchanged)]
    [TestCase(PendingFinalStorageChange.Delete)]
    [TestCase(PendingFinalStorageChange.Clear)]
    public async Task FinishWithPendingDistinctPhaseTouches_MatchesPatricia(
        PendingFinalStorageChange change)
    {
        StorageTestState state = BuildStorageState();
        Address blockerAddress = state.State.Addresses[0];
        Account blockerAccount = new(90, (UInt256)90_000);
        byte[] changedValue = change switch
        {
            PendingFinalStorageChange.Changed => [0x55],
            PendingFinalStorageChange.Delete => [0],
            _ => [0x22],
        };

        if (change is PendingFinalStorageChange.Clear)
            state.StorageTree.Clear();
        else if (change is not PendingFinalStorageChange.Unchanged)
            state.StorageTree.Set(state.ChangedSlot, changedValue);
        state.StorageTree.UpdateRootHash();
        state.StorageTree.Commit();
        Hash256 expectedStorageRoot = state.StorageTree.RootHash;
        Account expectedOwner = state.ParentAccount.WithChangedStorageRoot(expectedStorageRoot);
        state.State.StateTree.Set(
            blockerAddress.ToAccountPath.Bytes,
            AccountDecoder.Instance.Encode(blockerAccount).Bytes);
        state.State.StateTree.Set(
            state.Address.ToAccountPath.Bytes,
            AccountDecoder.Instance.Encode(expectedOwner).Bytes);
        state.State.StateTree.UpdateRootHash();
        state.State.StateTree.Commit();
        Hash256 expectedStateRoot = state.State.StateTree.RootHash;

        using BlockingCountingTrieNodeReader reader = new(state.State.Reader);
        reader.BlockNextStateRead();
        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.State.ParentRoot, reader);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(blockerAddress, blockerAccount)],
            []));
        if (!reader.WaitForBlockedStateRead(TimeSpan.FromSeconds(5)))
        {
            reader.ReleaseStateRead();
            Assert.Fail("The worker did not reach the blocking account proof.");
        }

        IReadOnlyList<SparseTrieFinalSlot> finalSlots = change is PendingFinalStorageChange.Clear
            ? []
            :
            [
                new SparseTrieFinalSlot(
                    state.UnchangedSlot,
                    state.UnchangedValue,
                    Changed: false),
                new SparseTrieFinalSlot(
                    state.ChangedSlot,
                    changedValue,
                    Changed: change is not PendingFinalStorageChange.Unchanged),
            ];
        Task<SparseTrieBlockResult> finishTask;
        try
        {
            block.EnqueueDelta(new SparseTriePhaseDelta(
                [],
                [new SparseTrieStorageDelta(
                    state.Address,
                    state.ParentStorageRoot,
                    state.UnchangedSlot,
                    [0xa1])]));
            block.EnqueueDelta(new SparseTriePhaseDelta(
                [],
                [new SparseTrieStorageDelta(
                    state.Address,
                    state.ParentStorageRoot,
                    state.ChangedSlot,
                    [0xa2])]));
            finishTask = block.FinishAsync(new SparseTrieFinalState(
                [new SparseTrieFinalStorageBatch(
                    state.Address,
                    state.ParentStorageRoot,
                    Clear: change is PendingFinalStorageChange.Clear,
                    finalSlots)],
                [
                    new SparseTrieFinalAccount(blockerAddress, blockerAccount),
                    new SparseTrieFinalAccount(state.Address, state.ParentAccount),
                ]));
        }
        finally
        {
            reader.ReleaseStateRead();
        }

        SparseTrieBlockResult result = await finishTask;
        await block.PrepareCommitAsync();
        await block.AcceptAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StorageRoots[state.Address], Is.EqualTo(expectedStorageRoot));
            Assert.That(result.StateRoot, Is.EqualTo(expectedStateRoot));
            if (change is PendingFinalStorageChange.Unchanged or PendingFinalStorageChange.Clear)
            {
                Assert.That(
                    reader.StorageLoads,
                    Is.Zero,
                    "A touch marker queued behind Finish must not reveal storage afterward.");
            }
        }
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
                [new SparseTrieStorageDelta(
                    state.Address,
                    state.ParentStorageRoot,
                    state.ChangedSlot,
                    [0x77])]));
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

    private static IEnumerable<TestCaseData> PersistentWorkerCases()
    {
        yield return new TestCaseData(17, 2, 2, 2)
            .SetName("PersistentWorker_SmallestMultiBlockMultiStorageCase");
        yield return new TestCaseData(7_331, 8, 6, 3)
            .SetName("PersistentWorker_BroadDeterministicCase");
    }

    [TestCaseSource(nameof(PersistentWorkerCases))]
    public async Task PersistentWorker_StreamedPhasesAndFinalReconciliation_MatchPatricia(
        int seed,
        int blockCount,
        int contractCount,
        int phaseCount)
    {
        MultiContractTestState state = BuildMultiContractState(contractCount, slotsPerContract: 4);
        await using SparseTrieWorker worker = CreateWorker();

        for (int blockNumber = 0; blockNumber < blockCount; blockNumber++)
        {
            Hash256 parentStateRoot = state.StateRoot;
            int selectedCount = Math.Min(3, contractCount);
            int[] selectedContracts = new int[selectedCount];
            for (int ordinal = 0; ordinal < selectedCount; ordinal++)
                selectedContracts[ordinal] = (blockNumber + ordinal) % contractCount;

            List<SparseTriePhaseDelta> phases = new(phaseCount);
            Dictionary<int, HashSet<int>> touchedSlots = new(selectedCount);
            for (int ordinal = 0; ordinal < selectedCount; ordinal++)
                touchedSlots.Add(selectedContracts[ordinal], []);

            for (int phase = 0; phase < phaseCount; phase++)
            {
                List<SparseTrieAccountDelta> accountDeltas = new(selectedCount);
                List<SparseTrieStorageDelta> storageDeltas = new(selectedCount * 2);

                for (int ordinal = 0; ordinal < selectedCount; ordinal++)
                {
                    int contractIndex = selectedContracts[ordinal];
                    MultiContractState contract = state.Contracts[contractIndex];
                    Account provisionalAccount = new(
                        contract.Account.Nonce + (ulong)(100 + phase),
                        (UInt256)(50_000 + blockNumber * 1_000 + phase * 100 + contractIndex),
                        contract.StorageRoot,
                        contract.Account.CodeHash);
                    accountDeltas.Add(new SparseTrieAccountDelta(contract.Address, provisionalAccount));

                    int stableSlot = (blockNumber + ordinal) % contract.Slots.Count;
                    int rotatingSlot = (stableSlot + phase + 1) % contract.Slots.Count;
                    AddProvisionalStorageDelta(
                        storageDeltas,
                        touchedSlots[contractIndex],
                        contract,
                        stableSlot,
                        DeterministicValue(seed, blockNumber, phase, contractIndex, stableSlot, salt: 11));
                    if (rotatingSlot != stableSlot)
                    {
                        AddProvisionalStorageDelta(
                            storageDeltas,
                            touchedSlots[contractIndex],
                            contract,
                            rotatingSlot,
                            DeterministicValue(seed, blockNumber, phase, contractIndex, rotatingSlot, salt: 29));
                    }
                }

                phases.Add(new SparseTriePhaseDelta(accountDeltas, storageDeltas));
            }

            List<SparseTrieFinalStorageBatch> finalStorage = new(selectedCount);
            List<SparseTrieFinalAccount> finalAccounts = new(selectedCount);
            Dictionary<int, Hash256> expectedStorageRoots = new(selectedCount);

            for (int ordinal = 0; ordinal < selectedCount; ordinal++)
            {
                int contractIndex = selectedContracts[ordinal];
                MultiContractState contract = state.Contracts[contractIndex];
                Hash256 parentStorageRoot = contract.StorageRoot;
                bool clear = blockNumber % 5 == 4 && ordinal == 0;
                if (clear)
                {
                    contract.StorageTree.Clear();
                    for (int slot = 0; slot < contract.Slots.Count; slot++)
                        contract.Slots[slot] = [0];
                }

                int stableSlot = (blockNumber + ordinal) % contract.Slots.Count;
                int[] finalSlotIndexes = new int[touchedSlots[contractIndex].Count];
                touchedSlots[contractIndex].CopyTo(finalSlotIndexes);
                Array.Sort(finalSlotIndexes);
                List<SparseTrieFinalSlot> finalSlots = new(finalSlotIndexes.Length);
                for (int slotOrdinal = 0; slotOrdinal < finalSlotIndexes.Length; slotOrdinal++)
                {
                    int slot = finalSlotIndexes[slotOrdinal];
                    byte[] finalValue;
                    if (!clear && slot == stableSlot)
                    {
                        finalValue = CloneValue(contract.Slots[slot]);
                    }
                    else if ((seed + blockNumber + contractIndex + slot) % 5 == 0)
                    {
                        finalValue = [0];
                    }
                    else
                    {
                        finalValue = DeterministicValue(
                            seed,
                            blockNumber,
                            phaseCount,
                            contractIndex,
                            slot,
                            salt: 47);
                    }

                    UInt256 storageSlot = (UInt256)slot;
                    contract.StorageTree.Set(storageSlot, finalValue);
                    contract.Slots[slot] = CloneValue(finalValue);
                    finalSlots.Add(new SparseTrieFinalSlot(storageSlot, CloneValue(finalValue)));
                }

                contract.StorageTree.UpdateRootHash();
                contract.StorageTree.Commit();
                Hash256 storageRoot = contract.StorageTree.RootHash;
                expectedStorageRoots.Add(contractIndex, storageRoot);

                Account finalAccount = (blockNumber + ordinal) % 4 == 0
                    ? contract.Account
                    : new Account(
                        contract.Account.Nonce + (ulong)(blockNumber + ordinal + 1),
                        (UInt256)(100_000 + seed + blockNumber * 100 + contractIndex),
                        contract.StorageRoot,
                        contract.Account.CodeHash);
                Account expectedAccount = finalAccount.WithChangedStorageRoot(storageRoot);
                state.StateTree.Set(
                    contract.Address.ToAccountPath.Bytes,
                    AccountDecoder.Instance.Encode(expectedAccount).Bytes);

                finalStorage.Add(new SparseTrieFinalStorageBatch(
                    contract.Address,
                    parentStorageRoot,
                    clear,
                    finalSlots));
                finalAccounts.Add(new SparseTrieFinalAccount(contract.Address, finalAccount));
                contract.Account = expectedAccount;
                contract.StorageRoot = storageRoot;
            }

            state.StateTree.UpdateRootHash();
            state.StateTree.Commit();
            Hash256 expectedStateRoot = state.StateTree.RootHash;

            await using SparseTrieBlockHandle block = worker.BeginBlock(parentStateRoot, state.Reader);
            foreach (SparseTriePhaseDelta phase in phases)
                block.EnqueueDelta(phase);

            SparseTrieBlockResult result = await block.FinishAsync(
                new SparseTrieFinalState(finalStorage, finalAccounts));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(
                    result.StateRoot,
                    Is.EqualTo(expectedStateRoot),
                    $"seed={seed}, block={blockNumber}, contracts={contractCount}, phases={phaseCount}");
                foreach (KeyValuePair<int, Hash256> expectedStorage in expectedStorageRoots)
                {
                    MultiContractState contract = state.Contracts[expectedStorage.Key];
                    Assert.That(
                        result.StorageRoots[contract.Address],
                        Is.EqualTo(expectedStorage.Value),
                        $"seed={seed}, block={blockNumber}, contract={expectedStorage.Key}");
                }
            }

            await block.PrepareCommitAsync();
            await block.AcceptAsync();
            state.StateRoot = expectedStateRoot;
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

    [Test]
    public async Task IdleStorageProofFailure_PoisonsOnlySessionAndWorkerCanRecover()
    {
        StorageTestState state = BuildStorageState();
        using ThrowingStorageTrieNodeReader reader = new(state.State.Reader);
        await using SparseTrieWorker worker = CreateWorker();

        await using (SparseTrieBlockHandle poisoned = worker.BeginBlock(
            state.State.ParentRoot,
            reader))
        {
            ValueHash256 slotPath = default;
            StorageTree.ComputeKeyWithLookup(state.ChangedSlot, ref slotPath);
            Assert.That(
                poisoned.TryEnqueueStorageTouch(
                    state.Address.ToAccountPath.ToCommitment(),
                    state.ParentStorageRoot,
                    slotPath),
                Is.True);
            Assert.That(reader.WaitForStorageRead(TimeSpan.FromSeconds(5)), Is.True);

            Func<Task> finish = async () => await poisoned.FinishAsync(
                new SparseTrieFinalState([], []));
            Assert.ThrowsAsync<InvalidOperationException>(finish);
            await poisoned.AbortAsync();
        }

        await using SparseTrieBlockHandle recovered = worker.BeginBlock(
            state.State.ParentRoot,
            state.State.Reader);
        SparseTrieBlockResult recoveredResult = await recovered.FinishAsync(
            new SparseTrieFinalState([], []));
        await recovered.PrepareCommitAsync();
        await recovered.AcceptAsync();

        Assert.That(recoveredResult.StateRoot, Is.EqualTo(state.State.ParentRoot));
    }

    [Test]
    public async Task SnapshotStorageProofs_AreBoundedAndFinishDrainsWhileOwnerRemainsResponsive()
    {
        MultiContractTestState state = BuildMultiContractState(contractCount: 4, slotsPerContract: 4);
        using BlockingSnapshotPersistenceReader reader = new(state.NodeStorage);
        using SnapshotBundle bundle = CreateSnapshotBundle(reader);
        reader.BlockStorageReads();
        await using SparseTrieWorker worker = CreateWorker(storageProofWorkerCount: 2);
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.StateRoot, bundle);

        for (int i = 0; i < state.Contracts.Length; i++)
            EnqueueStorageTouch(block, state.Contracts[i], slot: 0);

        Assert.That(reader.WaitForActiveStorageReads(2, TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(block.TryEnqueueAccountTouch(state.Contracts[3].Address.ToAccountPath), Is.True);
        Assert.That(
            SpinWait.SpinUntil(() => reader.StateReads != 0, TimeSpan.FromSeconds(5)),
            Is.True,
            "The owner did not process account work while storage proofs were blocked.");

        Task<SparseTrieBlockResult> finishTask = block.FinishAsync(new SparseTrieFinalState([], []));
        Task first = await Task.WhenAny(finishTask, Task.Delay(TimeSpan.FromMilliseconds(100)));
        Assert.That(first, Is.Not.SameAs(finishTask), "Finish did not wait for in-flight storage proofs.");

        reader.ReleaseStorageReads();
        SparseTrieBlockResult result = await finishTask;
        await block.PrepareCommitAsync();
        await block.AcceptAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(state.StateRoot));
            Assert.That(reader.MaxActiveStorageReads, Is.EqualTo(2));
            Assert.That(reader.MaxActiveReadsPerAccount, Is.EqualTo(1));
            Assert.That(reader.DisposedDuringActiveRead, Is.False);
        }
    }

    [Test]
    public async Task SnapshotStorageProofs_SerializeSameAccountAndQueuedFinishWins()
    {
        MultiContractTestState state = BuildMultiContractState(contractCount: 1, slotsPerContract: 4);
        MultiContractState contract = state.Contracts[0];
        using BlockingSnapshotPersistenceReader reader = new(state.NodeStorage);
        using SnapshotBundle bundle = CreateSnapshotBundle(reader);
        reader.BlockStorageReads();
        await using SparseTrieWorker worker = CreateWorker(storageProofWorkerCount: 2);
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.StateRoot, bundle);

        EnqueueStorageTouch(block, contract, slot: 0);
        Assert.That(reader.WaitForActiveStorageReads(1, TimeSpan.FromSeconds(5)), Is.True);
        EnqueueStorageTouch(block, contract, slot: 1);
        Assert.That(
            SpinWait.SpinUntil(() => reader.MaxActiveReadsPerAccount > 1, TimeSpan.FromMilliseconds(100)),
            Is.False,
            "Two proof jobs mutated the same storage trie concurrently.");

        Task<SparseTrieBlockResult> finishTask = block.FinishAsync(new SparseTrieFinalState([], []));
        reader.ReleaseStorageReads();
        SparseTrieBlockResult result = await finishTask;
        await block.PrepareCommitAsync();
        await block.AcceptAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(state.StateRoot));
            Assert.That(reader.MaxActiveReadsPerAccount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task SnapshotStorageProofs_AbortDrainsBeforeBundleCanBeDisposed()
    {
        MultiContractTestState state = BuildMultiContractState(contractCount: 2, slotsPerContract: 4);
        using BlockingSnapshotPersistenceReader reader = new(state.NodeStorage);
        using SnapshotBundle bundle = CreateSnapshotBundle(reader);
        reader.BlockStorageReads();
        await using SparseTrieWorker worker = CreateWorker(storageProofWorkerCount: 2);
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.StateRoot, bundle);

        EnqueueStorageTouch(block, state.Contracts[0], slot: 0);
        EnqueueStorageTouch(block, state.Contracts[1], slot: 0);
        Assert.That(reader.WaitForActiveStorageReads(2, TimeSpan.FromSeconds(5)), Is.True);

        Task abortTask = block.AbortAsync();
        Task first = await Task.WhenAny(abortTask, Task.Delay(TimeSpan.FromMilliseconds(100)));
        Assert.That(first, Is.Not.SameAs(abortTask), "Abort did not wait for in-flight storage proofs.");

        reader.ReleaseStorageReads();
        await abortTask;
        bundle.Dispose();
        Assert.That(reader.DisposedDuringActiveRead, Is.False);
    }

    [Test]
    public async Task SnapshotStorageProofFailure_PoisonsOnlySessionAndWorkerCanRecover()
    {
        MultiContractTestState state = BuildMultiContractState(contractCount: 1, slotsPerContract: 4);
        await using SparseTrieWorker worker = CreateWorker(storageProofWorkerCount: 2);

        using (BlockingSnapshotPersistenceReader failingReader = new(state.NodeStorage, failStorageReads: true))
        using (SnapshotBundle failingBundle = CreateSnapshotBundle(failingReader))
        await using (SparseTrieBlockHandle poisoned = worker.BeginBlock(state.StateRoot, failingBundle))
        {
            EnqueueStorageTouch(poisoned, state.Contracts[0], slot: 0);
            Assert.That(
                SpinWait.SpinUntil(() => failingReader.StorageReads != 0, TimeSpan.FromSeconds(5)),
                Is.True);

            Func<Task> finish = async () => await poisoned.FinishAsync(new SparseTrieFinalState([], []));
            Assert.ThrowsAsync<InvalidOperationException>(finish);
            await poisoned.AbortAsync();
        }

        using BlockingSnapshotPersistenceReader recoveredReader = new(state.NodeStorage);
        using SnapshotBundle recoveredBundle = CreateSnapshotBundle(recoveredReader);
        await using SparseTrieBlockHandle recovered = worker.BeginBlock(state.StateRoot, recoveredBundle);
        SparseTrieBlockResult recoveredResult = await recovered.FinishAsync(new SparseTrieFinalState([], []));
        await recovered.PrepareCommitAsync();
        await recovered.AcceptAsync();

        Assert.That(recoveredResult.StateRoot, Is.EqualTo(state.StateRoot));
    }

    private static SparseTrieWorker CreateWorker() =>
        new(LimboLogs.Instance.GetClassLogger<SparseTrieTaskTests>(), CancellationToken.None);

    private static SparseTrieWorker CreateWorker(int storageProofWorkerCount) =>
        new(
            LimboLogs.Instance.GetClassLogger<SparseTrieTaskTests>(),
            CancellationToken.None,
            maxRetainedNodes: 4_000_000,
            storageProofWorkerCount);

    private static SnapshotBundle CreateSnapshotBundle(IPersistence.IPersistenceReader reader)
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        ReadOnlySnapshotBundle readOnlyBundle = new(
            new SnapshotPooledList(1),
            reader,
            recordDetailedMetrics: false,
            PersistedSnapshotStack.Empty());
        return new SnapshotBundle(
            readOnlyBundle,
            Substitute.For<ITrieNodeCache>(),
            pool,
            ResourcePool.Usage.MainBlockProcessing);
    }

    private static void EnqueueStorageTouch(
        SparseTrieBlockHandle block,
        MultiContractState contract,
        int slot)
    {
        ValueHash256 slotPath = default;
        UInt256 storageSlot = (UInt256)slot;
        StorageTree.ComputeKeyWithLookup(storageSlot, ref slotPath);
        Assert.That(
            block.TryEnqueueStorageTouch(
                contract.Address.ToAccountPath.ToCommitment(),
                contract.StorageRoot,
                slotPath),
            Is.True);
    }

    public enum PendingFinalStorageChange
    {
        Changed,
        Unchanged,
        Delete,
        Clear,
    }

    private static void AddProvisionalStorageDelta(
        List<SparseTrieStorageDelta> deltas,
        HashSet<int> touchedSlots,
        MultiContractState contract,
        int slot,
        byte[] value)
    {
        touchedSlots.Add(slot);
        deltas.Add(new SparseTrieStorageDelta(
            contract.Address,
            contract.StorageRoot,
            (UInt256)slot,
            value));
    }

    private static byte[] DeterministicValue(
        int seed,
        int block,
        int phase,
        int contract,
        int slot,
        int salt)
    {
        int value = unchecked(
            seed * 31 + block * 43 + phase * 59 + contract * 71 + slot * 89 + salt * 101);
        return [(byte)(1 + (value & 0xfe))];
    }

    private static byte[] CloneValue(byte[] value) =>
        (byte[])value.Clone();

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

    private static MultiContractTestState BuildMultiContractState(
        int contractCount,
        int slotsPerContract)
    {
        MemDb db = new();
        RecordingNodeStorage nodeStorage = new(new NodeStorage(db));
        RawTrieStore store = new(nodeStorage);
        PatriciaTree stateTree = new(store.GetTrieStore(null), LimboLogs.Instance);
        MultiContractState[] contracts = new MultiContractState[contractCount];

        for (int contractIndex = 0; contractIndex < contractCount; contractIndex++)
        {
            UInt256 addressNumber = (UInt256)(10_000 + contractIndex);
            Address address = Address.FromNumber(in addressNumber);
            Hash256 addressHash = address.ToAccountPath.ToCommitment();
            StorageTree storageTree = new(store.GetTrieStore(addressHash), LimboLogs.Instance);
            Dictionary<int, byte[]> slots = new(slotsPerContract);
            for (int slot = 0; slot < slotsPerContract; slot++)
            {
                byte[] value = [(byte)(1 + contractIndex * slotsPerContract + slot)];
                UInt256 storageSlot = (UInt256)slot;
                storageTree.Set(storageSlot, value);
                slots.Add(slot, value);
            }
            storageTree.UpdateRootHash();
            storageTree.Commit();

            Hash256 codeHash = Keccak.Compute([(byte)(0xa0 + contractIndex)]);
            Account account = new(
                (ulong)contractIndex,
                (UInt256)(1_000 + contractIndex),
                storageTree.RootHash,
                codeHash);
            stateTree.Set(address.ToAccountPath.Bytes, AccountDecoder.Instance.Encode(account).Bytes);
            contracts[contractIndex] = new MultiContractState(
                address,
                account,
                storageTree,
                storageTree.RootHash,
                slots);
        }

        stateTree.UpdateRootHash();
        stateTree.Commit();
        return new MultiContractTestState(
            stateTree,
            contracts,
            stateTree.RootHash,
            new HalfPathTrieNodeReader(nodeStorage),
            nodeStorage);
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

    private sealed class MultiContractTestState(
        PatriciaTree stateTree,
        MultiContractState[] contracts,
        Hash256 stateRoot,
        HalfPathTrieNodeReader reader,
        RecordingNodeStorage nodeStorage)
    {
        public PatriciaTree StateTree { get; } = stateTree;
        public MultiContractState[] Contracts { get; } = contracts;
        public Hash256 StateRoot { get; set; } = stateRoot;
        public HalfPathTrieNodeReader Reader { get; } = reader;
        public RecordingNodeStorage NodeStorage { get; } = nodeStorage;
    }

    private sealed class MultiContractState(
        Address address,
        Account account,
        StorageTree storageTree,
        Hash256 storageRoot,
        Dictionary<int, byte[]> slots)
    {
        public Address Address { get; } = address;
        public Account Account { get; set; } = account;
        public StorageTree StorageTree { get; } = storageTree;
        public Hash256 StorageRoot { get; set; } = storageRoot;
        public Dictionary<int, byte[]> Slots { get; } = slots;
    }

    private sealed class RecordingNodeStorage(INodeStorage inner) : INodeStorage
    {
        private readonly ConcurrentDictionary<(Hash256? Address, TreePath Path), byte[]> _nodes = new();

        public INodeStorage.KeyScheme Scheme
        {
            get => inner.Scheme;
            set => inner.Scheme = value;
        }

        public bool RequirePath => inner.RequirePath;

        public byte[]? Get(
            Hash256? address,
            in TreePath path,
            in ValueHash256 keccak,
            ReadFlags readFlags = ReadFlags.None) =>
            inner.Get(address, path, keccak, readFlags);

        public void Set(
            Hash256? address,
            in TreePath path,
            in ValueHash256 hash,
            ReadOnlySpan<byte> data,
            WriteFlags writeFlags = WriteFlags.None)
        {
            inner.Set(address, path, hash, data, writeFlags);
            Record(address, path, data);
        }

        public INodeStorage.IWriteBatch StartWriteBatch() =>
            new RecordingWriteBatch(this, inner.StartWriteBatch());

        public bool KeyExists(
            in ValueHash256? address,
            in TreePath path,
            in ValueHash256 hash) =>
            inner.KeyExists(address, path, hash);

        public void Flush(bool onlyWal) => inner.Flush(onlyWal);

        public void Compact() => inner.Compact();

        public byte[]? TryGetLatest(Hash256? address, in TreePath path) =>
            _nodes.TryGetValue((address, path), out byte[]? rlp) ? rlp : null;

        private void Record(Hash256? address, in TreePath path, ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                _nodes.TryRemove((address, path), out _);
            else
                _nodes[(address, path)] = data.ToArray();
        }

        private sealed class RecordingWriteBatch(
            RecordingNodeStorage parent,
            INodeStorage.IWriteBatch innerBatch) : INodeStorage.IWriteBatch
        {
            public void Set(
                Hash256? address,
                in TreePath path,
                in ValueHash256 currentNodeKeccak,
                ReadOnlySpan<byte> data,
                WriteFlags writeFlags)
            {
                innerBatch.Set(address, path, currentNodeKeccak, data, writeFlags);
                parent.Record(address, path, data);
            }

            public void Dispose() => innerBatch.Dispose();
        }
    }

    private sealed class BlockingSnapshotPersistenceReader(
        RecordingNodeStorage nodeStorage,
        bool failStorageReads = false) : IPersistence.IPersistenceReader
    {
        private readonly ManualResetEventSlim _releaseStorageReads = new(initialState: true);
        private readonly ConcurrentDictionary<Hash256, int> _activeStorageReadsByAccount = new();
        private int _activeStorageReads;
        private int _maxActiveStorageReads;
        private int _maxActiveReadsPerAccount;
        private int _stateReads;
        private int _storageReads;
        private int _disposed;
        private int _disposedDuringActiveRead;

        public int MaxActiveStorageReads => Volatile.Read(ref _maxActiveStorageReads);
        public int MaxActiveReadsPerAccount => Volatile.Read(ref _maxActiveReadsPerAccount);
        public int StateReads => Volatile.Read(ref _stateReads);
        public int StorageReads => Volatile.Read(ref _storageReads);
        public bool DisposedDuringActiveRead => Volatile.Read(ref _disposedDuringActiveRead) != 0;

        public void BlockStorageReads() => _releaseStorageReads.Reset();

        public void ReleaseStorageReads() => _releaseStorageReads.Set();

        public bool WaitForActiveStorageReads(int count, TimeSpan timeout) =>
            SpinWait.SpinUntil(() => Volatile.Read(ref _activeStorageReads) >= count, timeout);

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags)
        {
            Interlocked.Increment(ref _stateReads);
            return nodeStorage.TryGetLatest(address: null, path);
        }

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags)
        {
            Interlocked.Increment(ref _storageReads);
            int active = Interlocked.Increment(ref _activeStorageReads);
            int activeForAccount = _activeStorageReadsByAccount.AddOrUpdate(
                address,
                addValue: 1,
                static (_, current) => current + 1);
            UpdateMaximum(ref _maxActiveStorageReads, active);
            UpdateMaximum(ref _maxActiveReadsPerAccount, activeForAccount);

            try
            {
                if (failStorageReads)
                    throw new InvalidOperationException("Expected storage proof failure.");
                _releaseStorageReads.Wait();
                return nodeStorage.TryGetLatest(address, path);
            }
            finally
            {
                _activeStorageReadsByAccount.AddOrUpdate(
                    address,
                    addValue: 0,
                    static (_, current) => current - 1);
                Interlocked.Decrement(ref _activeStorageReads);
            }
        }

        private static void UpdateMaximum(ref int maximum, int value)
        {
            int current = Volatile.Read(ref maximum);
            while (current < value)
            {
                int observed = Interlocked.CompareExchange(ref maximum, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (Volatile.Read(ref _activeStorageReads) != 0)
                Volatile.Write(ref _disposedDuringActiveRead, 1);
            _releaseStorageReads.Set();
            _releaseStorageReads.Dispose();
        }

        public Account? GetAccount(Address address) => null;
        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue) => false;
        public StateId CurrentState => StateId.PreGenesis;
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;
        public bool TryGetStorageRaw(
            in ValueHash256 addrHash,
            in ValueHash256 slotHash,
            ref SlotValue value) => false;
        public IPersistence.IFlatIterator CreateAccountIterator(
            in ValueHash256 startKey,
            in ValueHash256 endKey) => throw new NotSupportedException();
        public IPersistence.IFlatIterator CreateStorageIterator(
            in ValueHash256 accountKey,
            in ValueHash256 startSlotKey,
            in ValueHash256 endSlotKey) => throw new NotSupportedException();
        public bool IsPreimageMode => false;
    }

    private class CountingTrieNodeReader(ITrieNodeReader inner) : ITrieNodeReader, IDisposable
    {
        private int _storageLoads;

        public int StorageLoads => Volatile.Read(ref _storageLoads);

        public virtual byte[] LoadStateRlp(
            in TreePath path,
            Hash256 hash,
            ReadFlags flags = ReadFlags.None) =>
            inner.LoadStateRlp(path, hash, flags);

        public virtual byte[] LoadStorageRlp(
            Hash256 accountPathHash,
            in TreePath path,
            Hash256 hash,
            ReadFlags flags = ReadFlags.None)
        {
            Interlocked.Increment(ref _storageLoads);
            return inner.LoadStorageRlp(accountPathHash, path, hash, flags);
        }

        public virtual void Dispose()
        {
        }
    }

    private sealed class BlockingCountingTrieNodeReader(ITrieNodeReader inner)
        : CountingTrieNodeReader(inner)
    {
        private readonly ManualResetEventSlim _stateReadStarted = new();
        private readonly ManualResetEventSlim _releaseStateRead = new(initialState: true);
        private int _blockStateRead;

        public void BlockNextStateRead()
        {
            _releaseStateRead.Reset();
            Volatile.Write(ref _blockStateRead, 1);
        }

        public bool WaitForBlockedStateRead(TimeSpan timeout) => _stateReadStarted.Wait(timeout);

        public void ReleaseStateRead() => _releaseStateRead.Set();

        public override byte[] LoadStateRlp(
            in TreePath path,
            Hash256 hash,
            ReadFlags flags = ReadFlags.None)
        {
            if (Interlocked.Exchange(ref _blockStateRead, 0) != 0)
            {
                _stateReadStarted.Set();
                _releaseStateRead.Wait();
            }

            return base.LoadStateRlp(path, hash, flags);
        }

        public override void Dispose()
        {
            _releaseStateRead.Set();
            _stateReadStarted.Dispose();
            _releaseStateRead.Dispose();
        }
    }

    private sealed class ThrowingStorageTrieNodeReader(ITrieNodeReader inner)
        : CountingTrieNodeReader(inner)
    {
        private readonly ManualResetEventSlim _storageRead = new();

        public bool WaitForStorageRead(TimeSpan timeout) => _storageRead.Wait(timeout);

        public override byte[] LoadStorageRlp(
            Hash256 accountPathHash,
            in TreePath path,
            Hash256 hash,
            ReadFlags flags = ReadFlags.None)
        {
            _storageRead.Set();
            throw new InvalidOperationException("Expected storage proof failure.");
        }

        public override void Dispose() => _storageRead.Dispose();
    }
}
