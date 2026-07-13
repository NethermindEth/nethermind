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
    public async Task WarmedAccountProof_AvoidsReaderFallback(bool proofBeforeDelta)
    {
        TestState state = BuildState(accountCount: 8);
        Address address = state.Addresses[3];
        ValueHash256 accountPath = address.ToAccountPath;
        List<WarmedTrieNode> proof = [];
        state.StateTree.WarmUpPath(accountPath.BytesAsSpan, proof);
        Account finalAccount = new(7, (UInt256)70_000);
        Hash256 expectedRoot = ApplyAccount(state.StateTree, address, finalAccount);
        ThrowingTrieNodeReader reader = new();

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, reader);
        if (proofBeforeDelta)
            Assert.That(block.TryEnqueueAccountTouch(accountPath, proof), Is.True);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(address, finalAccount)],
            []));
        if (!proofBeforeDelta)
            Assert.That(block.TryEnqueueAccountTouch(accountPath, proof), Is.True);

        SparseTrieFinalState finalState = new(
            [],
            [new SparseTrieFinalAccount(address, finalAccount)]);
        Assert.That(await block.PrepareFinalAsync(finalState), Is.False);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(expectedRoot));
            Assert.That(reader.StateLoads, Is.Zero);
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [Test]
    public async Task ReadOnlyProofBacklog_DoesNotDelayChangedProof()
    {
        TestState state = BuildState(accountCount: 80);
        Address changedAddress = state.Addresses[70];
        ValueHash256 changedPath = changedAddress.ToAccountPath;
        List<WarmedTrieNode> changedProof = [];
        state.StateTree.WarmUpPath(changedPath.BytesAsSpan, changedProof);
        Account finalAccount = new(71, (UInt256)710_000);
        Hash256 expectedRoot = ApplyAccount(state.StateTree, changedAddress, finalAccount);
        ThrowingTrieNodeReader reader = new();

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, reader);
        for (int i = 0; i < 64; i++)
        {
            Assert.That(
                block.TryEnqueueAccountTouch(
                    state.Addresses[i].ToAccountPath,
                    [new WarmedTrieNode(TreePath.Empty, [0xff])]),
                Is.True);
        }

        Assert.That(block.TryEnqueueAccountTouch(changedPath, changedProof), Is.True);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(changedAddress, finalAccount)],
            []));

        SparseTrieBlockResult result = await block.FinishAsync(
            new SparseTrieFinalState([], [new SparseTrieFinalAccount(changedAddress, finalAccount)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(expectedRoot));
            Assert.That(reader.StateLoads, Is.Zero);
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WarmedStorageProof_AvoidsReaderFallback(bool proofBeforeDelta)
    {
        StorageTestState state = BuildStorageState();
        ValueHash256 accountPath = state.Address.ToAccountPath;
        ValueHash256 slotPath = default;
        StorageTree.ComputeKeyWithLookup(state.ChangedSlot, ref slotPath);
        List<WarmedTrieNode> accountProof = [];
        List<WarmedTrieNode> storageProof = [];
        state.State.StateTree.WarmUpPath(accountPath.BytesAsSpan, accountProof);
        state.StorageTree.WarmUpPath(slotPath.BytesAsSpan, storageProof);

        byte[] finalValue = [0x55];
        state.StorageTree.Set(state.ChangedSlot, finalValue);
        state.StorageTree.UpdateRootHash();
        state.StorageTree.Commit();
        Hash256 expectedStorageRoot = state.StorageTree.RootHash;
        Account expectedAccount = state.ParentAccount.WithChangedStorageRoot(expectedStorageRoot);
        Hash256 expectedStateRoot = ApplyAccount(state.State.StateTree, state.Address, expectedAccount);
        ThrowingTrieNodeReader reader = new();

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.State.ParentRoot, reader);
        if (proofBeforeDelta)
            EnqueueStorageProofs(block, accountPath, state.ParentStorageRoot, slotPath, accountProof, storageProof);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [],
            [new SparseTrieStorageDelta(
                state.Address,
                state.ParentStorageRoot,
                state.ChangedSlot,
                finalValue)]));
        if (!proofBeforeDelta)
            EnqueueStorageProofs(block, accountPath, state.ParentStorageRoot, slotPath, accountProof, storageProof);

        SparseTrieFinalState finalState = new(
            [new SparseTrieFinalStorageBatch(
                state.Address,
                state.ParentStorageRoot,
                Clear: false,
                [new SparseTrieFinalSlot(state.ChangedSlot, finalValue)])],
            [new SparseTrieFinalAccount(state.Address, state.ParentAccount)]);
        Assert.That(await block.PrepareFinalAsync(finalState), Is.False);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StorageRoots[state.Address], Is.EqualTo(expectedStorageRoot));
            Assert.That(result.StateRoot, Is.EqualTo(expectedStateRoot));
            Assert.That(reader.StateLoads, Is.Zero);
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [Test]
    public async Task OverlappingAccountAndStorageProofs_RevealWithoutReaderFallback()
    {
        StorageTestState state = BuildStorageState();
        Address changedAccountAddress = state.State.Addresses[3];
        ValueHash256 contractPath = state.Address.ToAccountPath;
        ValueHash256 changedAccountPath = changedAccountAddress.ToAccountPath;
        ValueHash256 firstSlotPath = default;
        ValueHash256 secondSlotPath = default;
        StorageTree.ComputeKeyWithLookup(state.UnchangedSlot, ref firstSlotPath);
        StorageTree.ComputeKeyWithLookup(state.ChangedSlot, ref secondSlotPath);

        List<WarmedTrieNode> contractProof = [];
        List<WarmedTrieNode> changedAccountProof = [];
        List<WarmedTrieNode> firstSlotProof = [];
        List<WarmedTrieNode> secondSlotProof = [];
        state.State.StateTree.WarmUpPath(contractPath.BytesAsSpan, contractProof);
        state.State.StateTree.WarmUpPath(changedAccountPath.BytesAsSpan, changedAccountProof);
        state.StorageTree.WarmUpPath(firstSlotPath.BytesAsSpan, firstSlotProof);
        state.StorageTree.WarmUpPath(secondSlotPath.BytesAsSpan, secondSlotProof);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProofsOverlap(contractProof, changedAccountProof), Is.True);
            Assert.That(ProofsOverlap(firstSlotProof, secondSlotProof), Is.True);
        }

        byte[] firstValue = [0x55];
        byte[] secondValue = [0x66];
        Account changedAccount = new(37, (UInt256)370_000);
        state.StorageTree.Set(state.UnchangedSlot, firstValue);
        state.StorageTree.Set(state.ChangedSlot, secondValue);
        state.StorageTree.UpdateRootHash();
        state.StorageTree.Commit();
        Hash256 expectedStorageRoot = state.StorageTree.RootHash;
        Account expectedContract = state.ParentAccount.WithChangedStorageRoot(expectedStorageRoot);
        state.State.StateTree.Set(
            contractPath.Bytes,
            AccountDecoder.Instance.Encode(expectedContract).Bytes);
        state.State.StateTree.Set(
            changedAccountPath.Bytes,
            AccountDecoder.Instance.Encode(changedAccount).Bytes);
        state.State.StateTree.UpdateRootHash();
        state.State.StateTree.Commit();
        Hash256 expectedStateRoot = state.State.StateTree.RootHash;
        ThrowingTrieNodeReader reader = new();

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.State.ParentRoot, reader);
        Assert.That(block.TryEnqueueAccountTouch(contractPath, contractProof), Is.True);
        Assert.That(block.TryEnqueueAccountTouch(changedAccountPath, changedAccountProof), Is.True);
        Assert.That(
            block.TryEnqueueStorageTouch(
                contractPath.ToCommitment(),
                state.ParentStorageRoot,
                firstSlotPath,
                firstSlotProof),
            Is.True);
        Assert.That(
            block.TryEnqueueStorageTouch(
                contractPath.ToCommitment(),
                state.ParentStorageRoot,
                secondSlotPath,
                secondSlotProof),
            Is.True);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(changedAccountAddress, changedAccount)],
            [
                new SparseTrieStorageDelta(
                    state.Address,
                    state.ParentStorageRoot,
                    state.UnchangedSlot,
                    firstValue),
                new SparseTrieStorageDelta(
                    state.Address,
                    state.ParentStorageRoot,
                    state.ChangedSlot,
                    secondValue),
            ]));

        SparseTrieFinalState finalState = new(
            [new SparseTrieFinalStorageBatch(
                state.Address,
                state.ParentStorageRoot,
                Clear: false,
                [
                    new SparseTrieFinalSlot(state.UnchangedSlot, firstValue),
                    new SparseTrieFinalSlot(state.ChangedSlot, secondValue),
                ])],
            [
                new SparseTrieFinalAccount(state.Address, state.ParentAccount),
                new SparseTrieFinalAccount(changedAccountAddress, changedAccount),
            ]);
        Assert.That(await block.PrepareFinalAsync(finalState), Is.False);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StorageRoots[state.Address], Is.EqualTo(expectedStorageRoot));
            Assert.That(result.StateRoot, Is.EqualTo(expectedStateRoot));
            Assert.That(reader.StateLoads, Is.Zero);
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [Test]
    public async Task OverlappingAccountProofs_InSeparateBatches_DecodeSharedNodesOnce()
    {
        TestState state = BuildState(accountCount: 8);
        Address firstAddress = state.Addresses[2];
        Address secondAddress = state.Addresses[3];
        ValueHash256 firstPath = firstAddress.ToAccountPath;
        ValueHash256 secondPath = secondAddress.ToAccountPath;
        List<WarmedTrieNode> firstProof = [];
        List<WarmedTrieNode> secondProof = [];
        state.StateTree.WarmUpPath(firstPath.BytesAsSpan, firstProof);
        state.StateTree.WarmUpPath(secondPath.BytesAsSpan, secondProof);
        Assert.That(ProofsOverlap(firstProof, secondProof), Is.True);

        Account firstAccount = new(30, (UInt256)300_000);
        Account secondAccount = new(40, (UInt256)400_000);
        state.StateTree.Set(firstPath.Bytes, AccountDecoder.Instance.Encode(firstAccount).Bytes);
        state.StateTree.Set(secondPath.Bytes, AccountDecoder.Instance.Encode(secondAccount).Bytes);
        state.StateTree.UpdateRootHash();
        state.StateTree.Commit();
        Hash256 expectedRoot = state.StateTree.RootHash;
        ThrowingTrieNodeReader reader = new();

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, reader);
        using ManualResetEventSlim secondProofQueued = new();
        CallbackReadOnlyList<WarmedTrieNode> firstProofWithCallback = new(
            firstProof,
            () =>
            {
                try
                {
                    block.EnqueueDelta(new SparseTriePhaseDelta(
                        [new SparseTrieAccountDelta(secondAddress, secondAccount)],
                        []));
                    if (!block.TryEnqueueAccountTouch(secondPath, secondProof))
                        throw new InvalidOperationException("Failed to enqueue the second account proof.");
                }
                finally
                {
                    secondProofQueued.Set();
                }
            });

        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(firstAddress, firstAccount)],
            []));
        Assert.That(block.TryEnqueueAccountTouch(firstPath, firstProofWithCallback), Is.True);
        Assert.That(secondProofQueued.Wait(TimeSpan.FromSeconds(5)), Is.True);

        SparseTrieFinalState finalState = new(
            [],
            [
                new SparseTrieFinalAccount(firstAddress, firstAccount),
                new SparseTrieFinalAccount(secondAddress, secondAccount),
            ]);
        Assert.That(await block.PrepareFinalAsync(finalState), Is.False);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(expectedRoot));
            Assert.That(block.ProofNodeCount, Is.EqualTo(CountUniqueProofNodes(firstProof, secondProof)));
            Assert.That(reader.StateLoads, Is.Zero);
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [Test]
    public async Task OverlappingStorageProofs_InSeparateBatches_DecodeSharedNodesOnce()
    {
        StorageTestState state = BuildStorageState();
        ValueHash256 accountPath = state.Address.ToAccountPath;
        Hash256 accountHash = accountPath.ToCommitment();
        ValueHash256 firstSlotPath = default;
        ValueHash256 secondSlotPath = default;
        StorageTree.ComputeKeyWithLookup(state.UnchangedSlot, ref firstSlotPath);
        StorageTree.ComputeKeyWithLookup(state.ChangedSlot, ref secondSlotPath);
        List<WarmedTrieNode> accountProof = [];
        List<WarmedTrieNode> firstSlotProof = [];
        List<WarmedTrieNode> secondSlotProof = [];
        state.State.StateTree.WarmUpPath(accountPath.BytesAsSpan, accountProof);
        state.StorageTree.WarmUpPath(firstSlotPath.BytesAsSpan, firstSlotProof);
        state.StorageTree.WarmUpPath(secondSlotPath.BytesAsSpan, secondSlotProof);
        Assert.That(ProofsOverlap(firstSlotProof, secondSlotProof), Is.True);

        byte[] firstValue = [0x55];
        byte[] secondValue = [0x66];
        state.StorageTree.Set(state.UnchangedSlot, firstValue);
        state.StorageTree.Set(state.ChangedSlot, secondValue);
        state.StorageTree.UpdateRootHash();
        state.StorageTree.Commit();
        Hash256 expectedStorageRoot = state.StorageTree.RootHash;
        Account expectedAccount = state.ParentAccount.WithChangedStorageRoot(expectedStorageRoot);
        Hash256 expectedStateRoot = ApplyAccount(state.State.StateTree, state.Address, expectedAccount);
        ThrowingTrieNodeReader reader = new();

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.State.ParentRoot, reader);
        using ManualResetEventSlim secondProofQueued = new();
        CallbackReadOnlyList<WarmedTrieNode> firstProofWithCallback = new(
            firstSlotProof,
            () =>
            {
                try
                {
                    block.EnqueueDelta(new SparseTriePhaseDelta(
                        [],
                        [new SparseTrieStorageDelta(
                            state.Address,
                            state.ParentStorageRoot,
                            state.ChangedSlot,
                            secondValue)]));
                    if (!block.TryEnqueueStorageTouch(
                        accountHash,
                        state.ParentStorageRoot,
                        secondSlotPath,
                        secondSlotProof))
                    {
                        throw new InvalidOperationException("Failed to enqueue the second storage proof.");
                    }
                }
                finally
                {
                    secondProofQueued.Set();
                }
            });

        block.EnqueueDelta(new SparseTriePhaseDelta(
            [],
            [new SparseTrieStorageDelta(
                state.Address,
                state.ParentStorageRoot,
                state.UnchangedSlot,
                firstValue)]));
        Assert.That(block.TryEnqueueAccountTouch(accountPath, accountProof), Is.True);
        Assert.That(
            block.TryEnqueueStorageTouch(
                accountHash,
                state.ParentStorageRoot,
                firstSlotPath,
                firstProofWithCallback),
            Is.True);
        Assert.That(secondProofQueued.Wait(TimeSpan.FromSeconds(5)), Is.True);

        SparseTrieFinalState finalState = new(
            [new SparseTrieFinalStorageBatch(
                state.Address,
                state.ParentStorageRoot,
                Clear: false,
                [
                    new SparseTrieFinalSlot(state.UnchangedSlot, firstValue),
                    new SparseTrieFinalSlot(state.ChangedSlot, secondValue),
                ])],
            [new SparseTrieFinalAccount(state.Address, state.ParentAccount)]);
        Assert.That(await block.PrepareFinalAsync(finalState), Is.False);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StorageRoots[state.Address], Is.EqualTo(expectedStorageRoot));
            Assert.That(result.StateRoot, Is.EqualTo(expectedStateRoot));
            Assert.That(
                block.ProofNodeCount,
                Is.EqualTo(
                    CountUniqueProofNodes(accountProof) +
                    CountUniqueProofNodes(firstSlotProof, secondSlotProof)));
            Assert.That(reader.StateLoads, Is.Zero);
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [Test]
    public async Task StorageLatestWriteWins_AfterEarlierValueWasApplied()
    {
        StorageTestState state = BuildStorageState();
        ValueHash256 accountPath = state.Address.ToAccountPath;
        ValueHash256 slotPath = default;
        StorageTree.ComputeKeyWithLookup(state.ChangedSlot, ref slotPath);
        List<WarmedTrieNode> accountProof = [];
        List<WarmedTrieNode> storageProof = [];
        state.State.StateTree.WarmUpPath(accountPath.BytesAsSpan, accountProof);
        state.StorageTree.WarmUpPath(slotPath.BytesAsSpan, storageProof);
        byte[] firstValue = [0x44];
        byte[] finalValue = [0x55];

        state.StorageTree.Set(state.ChangedSlot, finalValue);
        state.StorageTree.UpdateRootHash();
        state.StorageTree.Commit();
        Hash256 expectedStorageRoot = state.StorageTree.RootHash;
        Account expectedAccount = state.ParentAccount.WithChangedStorageRoot(expectedStorageRoot);
        Hash256 expectedStateRoot = ApplyAccount(state.State.StateTree, state.Address, expectedAccount);
        ThrowingTrieNodeReader reader = new();

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.State.ParentRoot, reader);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [],
            [new SparseTrieStorageDelta(
                state.Address,
                state.ParentStorageRoot,
                state.ChangedSlot,
                firstValue)]));
        EnqueueStorageProofs(
            block,
            accountPath,
            state.ParentStorageRoot,
            slotPath,
            accountProof,
            storageProof);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [],
            [new SparseTrieStorageDelta(
                state.Address,
                state.ParentStorageRoot,
                state.ChangedSlot,
                finalValue)]));

        SparseTrieFinalState finalState = new(
            [new SparseTrieFinalStorageBatch(
                state.Address,
                state.ParentStorageRoot,
                Clear: false,
                [new SparseTrieFinalSlot(state.ChangedSlot, finalValue)])],
            [new SparseTrieFinalAccount(state.Address, state.ParentAccount)]);
        Assert.That(await block.PrepareFinalAsync(finalState), Is.False);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StorageRoots[state.Address], Is.EqualTo(expectedStorageRoot));
            Assert.That(result.StateRoot, Is.EqualTo(expectedStateRoot));
            Assert.That(reader.StateLoads, Is.Zero);
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [Test]
    public async Task AccountLatestWriteWins_WhileWaitingForProof()
    {
        TestState state = BuildState(accountCount: 8);
        Address address = state.Addresses[3];
        ValueHash256 accountPath = address.ToAccountPath;
        List<WarmedTrieNode> proof = [];
        state.StateTree.WarmUpPath(accountPath.BytesAsSpan, proof);
        Account firstAccount = new(17, (UInt256)170_000);
        Account finalAccount = new(18, (UInt256)180_000);
        Hash256 expectedRoot = ApplyAccount(state.StateTree, address, finalAccount);
        ThrowingTrieNodeReader reader = new();

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, reader);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(address, firstAccount)],
            []));
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(address, finalAccount)],
            []));
        Assert.That(block.TryEnqueueAccountTouch(accountPath, proof), Is.True);

        SparseTrieFinalState finalState = new(
            [],
            [new SparseTrieFinalAccount(address, finalAccount)]);
        Assert.That(await block.PrepareFinalAsync(finalState), Is.False);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(expectedRoot));
            Assert.That(reader.StateLoads, Is.Zero);
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [Test]
    public async Task ResidualFallback_ResolvesDeletionCollapseSibling()
    {
        TestState state = BuildState(accountCount: 2);
        Address address = state.Addresses[0];
        ValueHash256 accountPath = address.ToAccountPath;
        List<WarmedTrieNode> pathProof = [];
        state.StateTree.WarmUpPath(accountPath.BytesAsSpan, pathProof);
        SparseTrieFinalState finalState = new(
            [],
            [new SparseTrieFinalAccount(address, null)]);
        Hash256 expectedRoot = ApplyAccount(state.StateTree, address, null);
        using CountingTrieNodeReader reader = new(state.Reader);

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, reader);
        Assert.That(block.TryEnqueueAccountTouch(accountPath, pathProof), Is.True);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(address, null)],
            []));

        Assert.That(await block.PrepareFinalAsync(finalState), Is.True);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(expectedRoot));
            Assert.That(reader.StateLoads, Is.GreaterThan(0));
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [Test]
    public async Task ResidualFallback_CoversDeleteBeforeInsertCollapseSibling()
    {
        Dictionary<byte, Address> addressByFirstNibble = [];
        for (int i = 1; addressByFirstNibble.Count < 3; i++)
        {
            UInt256 number = (UInt256)i;
            Address candidate = Address.FromNumber(in number);
            addressByFirstNibble.TryAdd((byte)(candidate.ToAccountPath.Bytes[0] >> 4), candidate);
        }
        List<byte> nibbles = [.. addressByFirstNibble.Keys];
        nibbles.Sort();
        Address deletedAddress = addressByFirstNibble[nibbles[0]];
        Address siblingAddress = addressByFirstNibble[nibbles[1]];
        Address insertedAddress = addressByFirstNibble[nibbles[2]];
        Account deletedAccount = new(1, (UInt256)1_001);
        Account siblingAccount = new(2, (UInt256)1_002);
        Account insertedAccount = new(3, (UInt256)1_003);
        TestState state = BuildState(
            [(deletedAddress, deletedAccount), (siblingAddress, siblingAccount)]);
        SparseTrieFinalState finalState = new(
            [],
            [
                new SparseTrieFinalAccount(deletedAddress, null),
                new SparseTrieFinalAccount(insertedAddress, insertedAccount),
            ]);
        ApplyAccount(state.StateTree, deletedAddress, null);
        Hash256 expectedRoot = ApplyAccount(state.StateTree, insertedAddress, insertedAccount);
        using CountingTrieNodeReader reader = new(state.Reader);

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, reader);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [
                new SparseTrieAccountDelta(deletedAddress, null),
                new SparseTrieAccountDelta(insertedAddress, insertedAccount),
            ],
            []));

        Assert.That(await block.PrepareFinalAsync(finalState), Is.True);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(expectedRoot));
            Assert.That(reader.StateLoads, Is.GreaterThan(0));
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [Test]
    public async Task ResidualFallback_UpdatesBelowSurvivingCollapsedBranch()
    {
        Dictionary<byte, List<Address>> addressesByFirstNibble = [];
        Address deletedAddress = Address.Zero;
        Address revealedSurvivor = Address.Zero;
        Address residualSurvivor = Address.Zero;
        for (int i = 1; residualSurvivor == Address.Zero; i++)
        {
            UInt256 number = (UInt256)i;
            Address candidate = Address.FromNumber(in number);
            byte nibble = (byte)(candidate.ToAccountPath.Bytes[0] >> 4);
            if (!addressesByFirstNibble.TryGetValue(nibble, out List<Address>? group))
            {
                group = [];
                addressesByFirstNibble.Add(nibble, group);
            }
            group.Add(candidate);

            if (group.Count < 2)
                continue;
            foreach (KeyValuePair<byte, List<Address>> entry in addressesByFirstNibble)
            {
                if (entry.Key >= nibble)
                    continue;
                deletedAddress = entry.Value[0];
                revealedSurvivor = group[0];
                residualSurvivor = group[1];
                break;
            }
        }

        Account deletedAccount = new(1, (UInt256)1_001);
        Account revealedAccount = new(2, (UInt256)1_002);
        Account originalResidualAccount = new(3, (UInt256)1_003);
        Account finalResidualAccount = new(4, (UInt256)4_004);
        TestState state = BuildState(
            [
                (deletedAddress, deletedAccount),
                (revealedSurvivor, revealedAccount),
                (residualSurvivor, originalResidualAccount),
            ]);
        ApplyAccount(state.StateTree, deletedAddress, null);
        Hash256 expectedRoot = ApplyAccount(state.StateTree, residualSurvivor, finalResidualAccount);

        List<WarmedTrieNode> deletedProof = [];
        List<WarmedTrieNode> survivorProof = [];
        PatriciaTree parentTree = new(state.Store.GetTrieStore(null), LimboLogs.Instance)
        {
            RootHash = state.ParentRoot,
        };
        parentTree.WarmUpPath(deletedAddress.ToAccountPath.BytesAsSpan, deletedProof);
        parentTree.WarmUpPath(revealedSurvivor.ToAccountPath.BytesAsSpan, survivorProof);

        using CountingTrieNodeReader reader = new(state.Reader);
        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, reader);
        Assert.That(block.TryEnqueueAccountTouch(deletedAddress.ToAccountPath, deletedProof), Is.True);
        Assert.That(block.TryEnqueueAccountTouch(revealedSurvivor.ToAccountPath, survivorProof), Is.True);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [
                new SparseTrieAccountDelta(deletedAddress, null),
                new SparseTrieAccountDelta(residualSurvivor, finalResidualAccount),
            ],
            []));
        SparseTrieFinalState finalState = new(
            [],
            [
                new SparseTrieFinalAccount(deletedAddress, null),
                new SparseTrieFinalAccount(residualSurvivor, finalResidualAccount),
            ]);

        Assert.That(await block.PrepareFinalAsync(finalState), Is.True);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(expectedRoot));
            Assert.That(reader.StateLoads, Is.GreaterThan(0));
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [Test]
    public async Task ClearStorage_DropsUnrevealedParentDeltaAndAppliesFinalSlotsFromEmpty()
    {
        StorageTestState state = BuildStorageState();
        ValueHash256 accountPath = state.Address.ToAccountPath;
        List<WarmedTrieNode> accountProof = [];
        state.State.StateTree.WarmUpPath(accountPath.BytesAsSpan, accountProof);
        byte[] provisionalValue = [0x44];
        byte[] finalValue = [0x55];

        state.StorageTree.Clear();
        state.StorageTree.Set(state.ChangedSlot, finalValue);
        state.StorageTree.UpdateRootHash();
        state.StorageTree.Commit();
        Hash256 expectedStorageRoot = state.StorageTree.RootHash;
        Account expectedAccount = state.ParentAccount.WithChangedStorageRoot(expectedStorageRoot);
        Hash256 expectedStateRoot = ApplyAccount(state.State.StateTree, state.Address, expectedAccount);
        ThrowingTrieNodeReader reader = new();

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.State.ParentRoot, reader);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [],
            [new SparseTrieStorageDelta(
                state.Address,
                state.ParentStorageRoot,
                state.ChangedSlot,
                provisionalValue)]));
        Assert.That(block.TryEnqueueAccountTouch(accountPath, accountProof), Is.True);

        SparseTrieFinalState finalState = new(
            [new SparseTrieFinalStorageBatch(
                state.Address,
                state.ParentStorageRoot,
                Clear: true,
                [new SparseTrieFinalSlot(state.ChangedSlot, finalValue)])],
            [new SparseTrieFinalAccount(state.Address, state.ParentAccount)]);
        Assert.That(await block.PrepareFinalAsync(finalState), Is.False);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StorageRoots[state.Address], Is.EqualTo(expectedStorageRoot));
            Assert.That(result.StateRoot, Is.EqualTo(expectedStateRoot));
            Assert.That(reader.StateLoads, Is.Zero);
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [Test]
    public async Task DeletedAccountWithoutStorageBatch_DropsUnrevealedProvisionalStorage()
    {
        StorageTestState state = BuildStorageState();
        Address address = state.Address;
        ValueHash256 accountPath = address.ToAccountPath;
        List<WarmedTrieNode> accountPathProof = [];
        state.State.StateTree.WarmUpPath(accountPath.BytesAsSpan, accountPathProof);
        SparseTrieFinalState finalState = new(
            [],
            [new SparseTrieFinalAccount(address, null)]);
        Hash256 expectedStateRoot = ApplyAccount(state.State.StateTree, address, null);
        using CountingTrieNodeReader reader = new(state.State.Reader);

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.State.ParentRoot, reader);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [],
            [new SparseTrieStorageDelta(
                address,
                state.ParentStorageRoot,
                state.ChangedSlot,
                [0x44])]));
        Assert.That(block.TryEnqueueAccountTouch(accountPath, accountPathProof), Is.True);

        Assert.That(await block.PrepareFinalAsync(finalState), Is.True);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(expectedStateRoot));
            Assert.That(result.StorageRoots, Is.Empty);
            Assert.That(reader.StateLoads, Is.GreaterThan(0));
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ResidualAccountFallback_MatchesPatricia(bool delete)
    {
        TestState state = BuildState(accountCount: 8);
        Address address = state.Addresses[3];
        Account? finalAccount = delete ? null : new Account(7, (UInt256)70_000);
        SparseTrieFinalState finalState = new(
            [],
            [new SparseTrieFinalAccount(address, finalAccount)]);
        Hash256 expectedRoot = ApplyAccount(state.StateTree, address, finalAccount);
        using CountingTrieNodeReader reader = new(state.Reader);

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, reader);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(address, finalAccount)],
            []));

        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(expectedRoot));
            Assert.That(reader.StateLoads, Is.GreaterThan(0));
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [Test]
    public async Task ResidualAccountFallback_MergesWithPreviouslyRevealedPath()
    {
        TestState state = BuildState(accountCount: 8);
        Address firstAddress = state.Addresses[2];
        Address secondAddress = state.Addresses[5];
        Account firstAccount = new(13, (UInt256)130_000);
        Account secondAccount = new(16, (UInt256)160_000);
        SparseTrieFinalState finalState = new(
            [],
            [
                new SparseTrieFinalAccount(firstAddress, firstAccount),
                new SparseTrieFinalAccount(secondAddress, secondAccount),
            ]);
        List<WarmedTrieNode> firstProof = [];
        state.StateTree.WarmUpPath(firstAddress.ToAccountPath.BytesAsSpan, firstProof);
        ApplyAccount(state.StateTree, firstAddress, firstAccount);
        Hash256 expectedRoot = ApplyAccount(state.StateTree, secondAddress, secondAccount);
        using CountingTrieNodeReader reader = new(state.Reader);

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, reader);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [
                new SparseTrieAccountDelta(firstAddress, firstAccount),
                new SparseTrieAccountDelta(secondAddress, secondAccount),
            ],
            []));
        Assert.That(
            block.TryEnqueueAccountTouch(firstAddress.ToAccountPath, firstProof),
            Is.True);

        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(expectedRoot));
            Assert.That(reader.StateLoads, Is.GreaterThan(0));
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ResidualStorageFallback_MatchesPatricia(bool delete)
    {
        StorageTestState state = BuildStorageState();
        byte[] finalValue = delete ? [0] : [0x55];
        SparseTrieFinalState finalState = new(
            [new SparseTrieFinalStorageBatch(
                state.Address,
                state.ParentStorageRoot,
                Clear: false,
                [new SparseTrieFinalSlot(state.ChangedSlot, finalValue)])],
            [new SparseTrieFinalAccount(state.Address, state.ParentAccount)]);
        state.StorageTree.Set(state.ChangedSlot, finalValue);
        state.StorageTree.UpdateRootHash();
        state.StorageTree.Commit();
        Hash256 expectedStorageRoot = state.StorageTree.RootHash;
        Account expectedAccount = state.ParentAccount.WithChangedStorageRoot(expectedStorageRoot);
        Hash256 expectedStateRoot = ApplyAccount(state.State.StateTree, state.Address, expectedAccount);
        using CountingTrieNodeReader reader = new(state.State.Reader);

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.State.ParentRoot, reader);
        List<WarmedTrieNode> accountProof = [];
        state.State.StateTree.WarmUpPath(state.Address.ToAccountPath.BytesAsSpan, accountProof);
        Assert.That(block.TryEnqueueAccountTouch(state.Address.ToAccountPath, accountProof), Is.True);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [],
            [new SparseTrieStorageDelta(
                state.Address,
                state.ParentStorageRoot,
                state.ChangedSlot,
                finalValue)]));

        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StorageRoots[state.Address], Is.EqualTo(expectedStorageRoot));
            Assert.That(result.StateRoot, Is.EqualTo(expectedStateRoot));
            Assert.That(reader.StateLoads, Is.Zero);
            Assert.That(reader.StorageLoads, Is.GreaterThan(0));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SnapshotBackedStorageUpdates_ForThreeAccounts_MatchPatricia(
        bool publishBeforeFinal)
    {
        const int contractCount = 3;
        MultiContractTestState state = BuildMultiContractState(contractCount, slotsPerContract: 4);
        ProofPersistenceReader persistenceReader = new(state);
        ReadOnlySnapshotBundle readOnlyBundle = new(
            new SnapshotPooledList(0),
            persistenceReader,
            recordDetailedMetrics: false,
            PersistedSnapshotStack.Empty());
        ResourcePool resourcePool = new(new FlatDbConfig { CompactSize = 8 });
        using SnapshotBundle bundle = new(
            readOnlyBundle,
            Substitute.For<ITrieNodeCache>(),
            resourcePool,
            ResourcePool.Usage.MainBlockProcessing);

        List<SparseTrieFinalStorageBatch> finalStorage = new(contractCount);
        List<SparseTrieFinalAccount> finalAccounts = new(contractCount);
        List<SparseTrieStorageDelta> storageDeltas = new(contractCount);
        List<SparseTrieAccountDelta> accountDeltas = new(contractCount);
        Dictionary<AddressAsKey, Hash256> expectedStorageRoots = new(
            contractCount,
            AddressAsKey.EqualityComparer);
        for (int i = 0; i < contractCount; i++)
        {
            MultiContractState contract = state.Contracts[i];
            UInt256 slot = (UInt256)(i + 1);
            byte[] finalValue = i == 0 ? [0] : [(byte)(0x70 + i)];
            contract.StorageTree.Set(slot, finalValue);
            contract.StorageTree.UpdateRootHash();
            contract.StorageTree.Commit();
            Hash256 expectedStorageRoot = contract.StorageTree.RootHash;
            expectedStorageRoots.Add(contract.Address, expectedStorageRoot);

            Account expectedAccount = contract.Account.WithChangedStorageRoot(expectedStorageRoot);
            state.StateTree.Set(
                contract.Address.ToAccountPath.Bytes,
                AccountDecoder.Instance.Encode(expectedAccount).Bytes);
            finalStorage.Add(new SparseTrieFinalStorageBatch(
                contract.Address,
                contract.StorageRoot,
                Clear: false,
                [new SparseTrieFinalSlot(slot, finalValue)]));
            finalAccounts.Add(new SparseTrieFinalAccount(contract.Address, contract.Account));
            storageDeltas.Add(new SparseTrieStorageDelta(
                contract.Address,
                contract.StorageRoot,
                slot,
                finalValue));
            accountDeltas.Add(new SparseTrieAccountDelta(contract.Address, contract.Account));
        }

        state.StateTree.UpdateRootHash();
        state.StateTree.Commit();
        Hash256 expectedStateRoot = state.StateTree.RootHash;
        SparseTrieFinalState finalState = new(finalStorage, finalAccounts);

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.StateRoot, bundle);
        if (publishBeforeFinal)
        {
            block.EnqueueDelta(new SparseTriePhaseDelta(accountDeltas, storageDeltas));
            Assert.That(
                SpinWait.SpinUntil(
                    () => persistenceReader.StateLoads > 0 &&
                        persistenceReader.StorageLoads >= contractCount,
                    TimeSpan.FromSeconds(5)),
                Is.True,
                "worker-idle exact proofs did not complete");
        }

        Assert.That(await block.PrepareFinalAsync(finalState), Is.EqualTo(!publishBeforeFinal));
        SparseTrieBlockResult result = await block.FinishAsync(finalState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(expectedStateRoot));
            for (int i = 0; i < contractCount; i++)
            {
                MultiContractState contract = state.Contracts[i];
                Assert.That(
                    result.StorageRoots[contract.Address],
                    Is.EqualTo(expectedStorageRoots[contract.Address]));
            }
            Assert.That(persistenceReader.StateLoads, Is.GreaterThan(0));
            Assert.That(persistenceReader.StorageLoads, Is.GreaterThanOrEqualTo(contractCount));
        }
    }

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
    public async Task RepeatedAccountPhases_AreReconciledAcrossBatchBoundary()
    {
        TestState state = BuildState(accountCount: 20);
        Address address = state.Addresses[3];
        Account originalAccount = new(3, (UInt256)1_003);

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, state.Reader);
        for (int phase = 0; phase < 98; phase++)
        {
            Account provisionalAccount = new(
                (ulong)(100 + phase),
                (UInt256)(100_000 + phase));
            block.EnqueueDelta(new SparseTriePhaseDelta(
                [new SparseTrieAccountDelta(address, provisionalAccount)],
                []));
        }

        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(address, null)],
            []));
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(address, new Account(999, (UInt256)999_000))],
            []));
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(address, originalAccount)],
            []));

        SparseTrieBlockResult result = await block.FinishAsync(
            new SparseTrieFinalState([], [new SparseTrieFinalAccount(address, originalAccount)]));
        await block.PrepareCommitAsync();
        await block.AcceptAsync();

        Assert.That(result.StateRoot, Is.EqualTo(state.ParentRoot));
    }

    [Test]
    public async Task DistinctAccountBatch_UsesWarmedProofsWithoutReaderFallback()
    {
        const int changedAccountCount = 100;
        TestState state = BuildState(accountCount: changedAccountCount + 4);
        List<List<WarmedTrieNode>> proofs = new(changedAccountCount);
        List<Account> accounts = new(changedAccountCount);
        List<SparseTrieFinalAccount> finalAccounts = new(changedAccountCount);

        for (int i = 0; i < changedAccountCount; i++)
        {
            Address address = state.Addresses[i];
            List<WarmedTrieNode> proof = [];
            state.StateTree.WarmUpPath(address.ToAccountPath.BytesAsSpan, proof);
            proofs.Add(proof);

            Account account = new((ulong)(1_000 + i), (UInt256)(1_000_000 + i));
            accounts.Add(account);
            finalAccounts.Add(new SparseTrieFinalAccount(address, account));
            state.StateTree.Set(
                address.ToAccountPath.Bytes,
                AccountDecoder.Instance.Encode(account).Bytes);
        }
        state.StateTree.UpdateRootHash();
        state.StateTree.Commit();
        Hash256 expectedRoot = state.StateTree.RootHash;
        ThrowingTrieNodeReader reader = new();

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, reader);
        for (int i = 0; i < changedAccountCount; i++)
        {
            Address address = state.Addresses[i];
            Assert.That(
                block.TryEnqueueAccountTouch(address.ToAccountPath, proofs[i]),
                Is.True);
            block.EnqueueDelta(new SparseTriePhaseDelta(
                [new SparseTrieAccountDelta(address, accounts[i])],
                []));
        }

        SparseTrieBlockResult result = await block.FinishAsync(
            new SparseTrieFinalState([], finalAccounts));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(expectedRoot));
            Assert.That(reader.StateLoads, Is.Zero);
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FinalStorageRootAccountDelta_IsOrderedAcrossFinalMarker(bool markerBeforeRoot)
    {
        TestState state = BuildState(accountCount: 20);
        Address address = state.Addresses[3];
        Account originalAccount = new(3, (UInt256)1_003);
        Account provisionalAccount = originalAccount.WithChangedStorageRoot(Keccak.Compute([0x11]));
        Account finalAccount = originalAccount.WithChangedStorageRoot(Keccak.Compute([0x22]));
        Hash256 expectedRoot = ApplyAccount(state.StateTree, address, finalAccount);

        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.ParentRoot, state.Reader);
        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(address, provisionalAccount)],
            []));
        if (markerBeforeRoot)
            block.EnqueueDelta(new SparseTriePhaseDelta([], [], IsFinal: true));

        block.EnqueueDelta(new SparseTriePhaseDelta(
            [new SparseTrieAccountDelta(address, finalAccount)],
            []));
        if (!markerBeforeRoot)
            block.EnqueueDelta(new SparseTriePhaseDelta([], [], IsFinal: true));

        SparseTrieBlockResult result = await block.FinishAsync(
            new SparseTrieFinalState([], [new SparseTrieFinalAccount(address, finalAccount)]));
        await block.PrepareCommitAsync();
        await block.AcceptAsync();

        Assert.That(result.StateRoot, Is.EqualTo(expectedRoot));
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
            Assert.That(
                block.TryEnqueueStorageTouch(accountHash, state.ParentStorageRoot, slotPath),
                Is.True);

        block.EnqueueDelta(delta);

        if (!warmerFirst)
            Assert.That(
                block.TryEnqueueStorageTouch(accountHash, state.ParentStorageRoot, slotPath),
                Is.True);

        block.EnqueueDelta(new SparseTriePhaseDelta([], [], IsFinal: true));
        SparseTrieFinalState finalState = new(
            [new SparseTrieFinalStorageBatch(
                state.Address,
                state.ParentStorageRoot,
                Clear: false,
                [new SparseTrieFinalSlot(state.ChangedSlot, finalValue)])],
            [new SparseTrieFinalAccount(state.Address, state.ParentAccount)]);
        Assert.That(await block.PrepareFinalAsync(finalState), Is.True);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);
        await block.PrepareCommitAsync();
        await block.AcceptAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StorageRoots[state.Address], Is.EqualTo(expectedStorageRoot));
            Assert.That(result.StateRoot, Is.EqualTo(expectedStateRoot));
            Assert.That(reader.StateLoads, Is.GreaterThan(0));
            Assert.That(reader.StorageLoads, Is.GreaterThan(0));
        }
    }

    [Test]
    public async Task NoProofTouches_AreDiscardedBeforeLongCommandBacklog()
    {
        StorageTestState state = BuildStorageState();
        ThrowingTrieNodeReader reader = new();
        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.State.ParentRoot, reader);

        Assert.That(block.TryEnqueueAccountTouch(state.Address.ToAccountPath), Is.True);
        ValueHash256 slotPath = default;
        StorageTree.ComputeKeyWithLookup(state.ChangedSlot, ref slotPath);
        Assert.That(
            block.TryEnqueueStorageTouch(
                state.Address.ToAccountPath.ToCommitment(),
                state.ParentStorageRoot,
                slotPath),
            Is.True);

        for (int i = 0; i < 16; i++)
            block.EnqueueDelta(new SparseTriePhaseDelta([], []));

        SparseTrieFinalState finalState = new([], []);
        Assert.That(await block.PrepareFinalAsync(finalState), Is.False);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(state.State.ParentRoot));
            Assert.That(reader.StateLoads, Is.Zero);
            Assert.That(reader.StorageLoads, Is.Zero);
        }
    }

    [Test]
    public async Task CloseSpeculativeIntake_RejectsLateTouchesAndDeltas()
    {
        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(
            Keccak.EmptyTreeHash,
            new ThrowingTrieNodeReader());
        ValueHash256 accountPath = Address.Zero.ToAccountPath;
        Hash256 accountHash = accountPath.ToCommitment();
        ValueHash256 slotPath = default;

        block.CloseSpeculativeIntake();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.TryEnqueueAccountTouch(accountPath), Is.False);
            Assert.That(block.TryEnqueueAccountTouch(accountPath, []), Is.False);
            Assert.That(
                block.TryEnqueueStorageTouch(accountHash, Keccak.EmptyTreeHash, slotPath),
                Is.False);
            Assert.That(
                block.TryEnqueueStorageTouch(accountHash, Keccak.EmptyTreeHash, slotPath, []),
                Is.False);
            Assert.That(
                () => block.EnqueueDelta(new SparseTriePhaseDelta([], [])),
                Throws.InvalidOperationException);
        }

        await block.AbortAsync();
    }

    [TestCase(PendingFinalStorageChange.Changed)]
    [TestCase(PendingFinalStorageChange.Unchanged)]
    [TestCase(PendingFinalStorageChange.Delete)]
    [TestCase(PendingFinalStorageChange.Clear)]
    public async Task FinishWithPendingDistinctPhaseDeltas_MatchesPatricia(
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
        {
            state.StorageTree.Clear();
            state.StorageTree.Set(state.ChangedSlot, changedValue);
        }
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

        using CountingTrieNodeReader reader = new(state.State.Reader);
        await using SparseTrieWorker worker = CreateWorker();
        await using SparseTrieBlockHandle block = worker.BeginBlock(state.State.ParentRoot, reader);
        for (int phase = 0; phase < 100; phase++)
        {
            block.EnqueueDelta(new SparseTriePhaseDelta(
                [new SparseTrieAccountDelta(blockerAddress, blockerAccount)],
                []));
        }

        IReadOnlyList<SparseTrieFinalSlot> finalSlots = change is PendingFinalStorageChange.Clear
            ? [new SparseTrieFinalSlot(state.ChangedSlot, changedValue)]
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
                change is PendingFinalStorageChange.Clear ? changedValue : [0xa2])]));
        SparseTrieFinalState finalState = new(
            [new SparseTrieFinalStorageBatch(
                state.Address,
                state.ParentStorageRoot,
                Clear: change is PendingFinalStorageChange.Clear,
                finalSlots)],
            [
                new SparseTrieFinalAccount(blockerAddress, blockerAccount),
                new SparseTrieFinalAccount(state.Address, state.ParentAccount),
            ]);
        Assert.That(await block.PrepareFinalAsync(finalState), Is.True);
        SparseTrieBlockResult result = await block.FinishAsync(finalState);
        await block.PrepareCommitAsync();
        await block.AcceptAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StorageRoots[state.Address], Is.EqualTo(expectedStorageRoot));
            Assert.That(result.StateRoot, Is.EqualTo(expectedStateRoot));
            Assert.That(reader.StateLoads, Is.GreaterThan(0));
            Assert.That(
                reader.StorageLoads,
                change is PendingFinalStorageChange.Clear ? Is.Zero : Is.GreaterThan(0));
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
    public async Task NoProofStorageTouch_DoesNotReadOrPoisonSession()
    {
        StorageTestState state = BuildStorageState();
        using ThrowingStorageTrieNodeReader reader = new(state.State.Reader);
        await using SparseTrieWorker worker = CreateWorker();

        await using (SparseTrieBlockHandle first = worker.BeginBlock(
            state.State.ParentRoot,
            reader))
        {
            ValueHash256 slotPath = default;
            StorageTree.ComputeKeyWithLookup(state.ChangedSlot, ref slotPath);
            Assert.That(
                first.TryEnqueueStorageTouch(
                    state.Address.ToAccountPath.ToCommitment(),
                    state.ParentStorageRoot,
                    slotPath),
                Is.True);
            SparseTrieFinalState finalState = new([], []);
            Assert.That(await first.PrepareFinalAsync(finalState), Is.False);
            SparseTrieBlockResult firstResult = await first.FinishAsync(finalState);
            await first.PrepareCommitAsync();
            await first.AcceptAsync();
            Assert.That(firstResult.StateRoot, Is.EqualTo(state.State.ParentRoot));
            Assert.That(reader.WaitForStorageRead(TimeSpan.Zero), Is.False);
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

    private static SparseTrieWorker CreateWorker() =>
        new(LimboLogs.Instance.GetClassLogger<SparseTrieTaskTests>(), CancellationToken.None);

    private static bool ProofsOverlap(
        IReadOnlyList<WarmedTrieNode> first,
        IReadOnlyList<WarmedTrieNode> second)
    {
        for (int i = 0; i < first.Count; i++)
        {
            for (int j = 0; j < second.Count; j++)
            {
                if (first[i].Path == second[j].Path)
                    return true;
            }
        }

        return false;
    }

    private static int CountUniqueProofNodes(params IReadOnlyList<WarmedTrieNode>[] proofs)
    {
        HashSet<TreePath> paths = [];
        for (int i = 0; i < proofs.Length; i++)
        {
            IReadOnlyList<WarmedTrieNode> proof = proofs[i];
            for (int j = 0; j < proof.Count; j++)
                paths.Add(proof[j].Path);
        }

        return paths.Count;
    }

    private static void EnqueueStorageProofs(
        SparseTrieBlockHandle block,
        ValueHash256 accountPath,
        Hash256 parentStorageRoot,
        ValueHash256 slotPath,
        IReadOnlyList<WarmedTrieNode> accountProof,
        IReadOnlyList<WarmedTrieNode> storageProof)
    {
        Assert.That(block.TryEnqueueAccountTouch(accountPath, accountProof), Is.True);
        Assert.That(
            block.TryEnqueueStorageTouch(
                accountPath.ToCommitment(),
                parentStorageRoot,
                slotPath,
                storageProof),
            Is.True);
    }

    private sealed class CallbackReadOnlyList<T>(IReadOnlyList<T> inner, Action callback) : IReadOnlyList<T>
    {
        private Action? _callback = callback;

        public int Count
        {
            get
            {
                InvokeCallback();
                return inner.Count;
            }
        }

        public T this[int index]
        {
            get
            {
                InvokeCallback();
                return inner[index];
            }
        }

        public IEnumerator<T> GetEnumerator() => inner.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();

        private void InvokeCallback() =>
            Interlocked.Exchange(ref _callback, null)?.Invoke();
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

    private static TestState BuildState(IReadOnlyList<(Address Address, Account Account)> accounts)
    {
        MemDb db = new();
        RawTrieStore store = new(db);
        PatriciaTree stateTree = new(store.GetTrieStore(null), LimboLogs.Instance);
        Address[] addresses = new Address[accounts.Count];
        for (int i = 0; i < accounts.Count; i++)
        {
            (Address address, Account account) = accounts[i];
            addresses[i] = address;
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
        RawTrieStore store = new(db);
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
            new HalfPathTrieNodeReader(new NodeStorage(db)));
    }

    private static Hash256 ApplyAccount(PatriciaTree tree, Address address, Account? account)
    {
        tree.Set(
            address.ToAccountPath.Bytes,
            account is null ? [] : AccountDecoder.Instance.Encode(account).Bytes);
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
        HalfPathTrieNodeReader reader)
    {
        public PatriciaTree StateTree { get; } = stateTree;
        public MultiContractState[] Contracts { get; } = contracts;
        public Hash256 StateRoot { get; set; } = stateRoot;
        public HalfPathTrieNodeReader Reader { get; } = reader;
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

    private sealed class ProofPersistenceReader : IPersistence.IPersistenceReader
    {
        private readonly Dictionary<TreePath, byte[]> _stateNodes = [];
        private readonly Dictionary<(Hash256 AccountHash, TreePath Path), byte[]> _storageNodes = [];
        private int _stateLoads;
        private int _storageLoads;

        public ProofPersistenceReader(MultiContractTestState state)
        {
            for (int i = 0; i < state.Contracts.Length; i++)
            {
                MultiContractState contract = state.Contracts[i];
                List<WarmedTrieNode> accountProof = [];
                state.StateTree.WarmUpPath(contract.Address.ToAccountPath.BytesAsSpan, accountProof);
                AddProof(_stateNodes, accountProof);

                Hash256 accountHash = contract.Address.ToAccountPath.ToCommitment();
                foreach (int slot in contract.Slots.Keys)
                {
                    ValueHash256 slotPath = default;
                    UInt256 storageSlot = (UInt256)slot;
                    StorageTree.ComputeKeyWithLookup(storageSlot, ref slotPath);
                    List<WarmedTrieNode> storageProof = [];
                    contract.StorageTree.WarmUpPath(slotPath.BytesAsSpan, storageProof);
                    for (int nodeIndex = 0; nodeIndex < storageProof.Count; nodeIndex++)
                    {
                        WarmedTrieNode node = storageProof[nodeIndex];
                        _storageNodes[(accountHash, node.Path)] = node.Rlp;
                    }
                }
            }
        }

        public int StateLoads => Volatile.Read(ref _stateLoads);
        public int StorageLoads => Volatile.Read(ref _storageLoads);
        public StateId CurrentState => new(0, Keccak.EmptyTreeHash);
        public bool IsPreimageMode => false;

        public Account? GetAccount(Address address) => null;

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue) => false;

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags)
        {
            Interlocked.Increment(ref _stateLoads);
            return _stateNodes.GetValueOrDefault(path);
        }

        public byte[]? TryLoadStorageRlp(
            Hash256 address,
            in TreePath path,
            ReadFlags flags)
        {
            Interlocked.Increment(ref _storageLoads);
            return _storageNodes.GetValueOrDefault((address, path));
        }

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

        public void Dispose()
        {
        }

        private static void AddProof(
            Dictionary<TreePath, byte[]> nodes,
            IReadOnlyList<WarmedTrieNode> proof)
        {
            for (int i = 0; i < proof.Count; i++)
            {
                WarmedTrieNode node = proof[i];
                nodes[node.Path] = node.Rlp;
            }
        }
    }

    private class CountingTrieNodeReader(ITrieNodeReader inner) : ITrieNodeReader, IDisposable
    {
        private int _stateLoads;
        private int _storageLoads;

        public int StateLoads => Volatile.Read(ref _stateLoads);
        public int StorageLoads => Volatile.Read(ref _storageLoads);

        public virtual byte[] LoadStateRlp(
            in TreePath path,
            Hash256 hash,
            ReadFlags flags = ReadFlags.None)
        {
            Interlocked.Increment(ref _stateLoads);
            return inner.LoadStateRlp(path, hash, flags);
        }

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
        private readonly ManualResetEventSlim _storageReadStarted = new();
        private readonly ManualResetEventSlim _releaseStorageRead = new(initialState: true);
        private int _blockStateRead;
        private int _blockStorageRead;

        public void BlockNextStateRead()
        {
            _releaseStateRead.Reset();
            Volatile.Write(ref _blockStateRead, 1);
        }

        public bool WaitForBlockedStateRead(TimeSpan timeout) => _stateReadStarted.Wait(timeout);

        public void ReleaseStateRead() => _releaseStateRead.Set();

        public void BlockNextStorageRead()
        {
            _releaseStorageRead.Reset();
            Volatile.Write(ref _blockStorageRead, 1);
        }

        public bool WaitForBlockedStorageRead(TimeSpan timeout) => _storageReadStarted.Wait(timeout);

        public void ReleaseStorageRead() => _releaseStorageRead.Set();

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

        public override byte[] LoadStorageRlp(
            Hash256 accountPathHash,
            in TreePath path,
            Hash256 hash,
            ReadFlags flags = ReadFlags.None)
        {
            if (Interlocked.Exchange(ref _blockStorageRead, 0) != 0)
            {
                _storageReadStarted.Set();
                _releaseStorageRead.Wait();
            }

            return base.LoadStorageRlp(accountPathHash, path, hash, flags);
        }

        public override void Dispose()
        {
            _releaseStateRead.Set();
            _releaseStorageRead.Set();
            _stateReadStarted.Dispose();
            _releaseStateRead.Dispose();
            _storageReadStarted.Dispose();
            _releaseStorageRead.Dispose();
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

    private sealed class ThrowingTrieNodeReader : ITrieNodeReader
    {
        private int _stateLoads;
        private int _storageLoads;

        public int StateLoads => Volatile.Read(ref _stateLoads);
        public int StorageLoads => Volatile.Read(ref _storageLoads);

        public byte[] LoadStateRlp(
            in TreePath path,
            Hash256 hash,
            ReadFlags flags = ReadFlags.None)
        {
            Interlocked.Increment(ref _stateLoads);
            throw new InvalidOperationException("Unexpected state proof fallback.");
        }

        public byte[] LoadStorageRlp(
            Hash256 accountPathHash,
            in TreePath path,
            Hash256 hash,
            ReadFlags flags = ReadFlags.None)
        {
            Interlocked.Increment(ref _storageLoads);
            throw new InvalidOperationException("Unexpected storage proof fallback.");
        }
    }
}
