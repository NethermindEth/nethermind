// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

public class WitnessGeneratingWorldState(
    IWorldState state,
    IStateReader stateReader,
    IReadOnlyTrieStore trieStore,
    WitnessHeaderRecorder headerRecorder,
    IHeaderFinder headerFinder)
    : WorldStateDecorator(state)
{
    private readonly Dictionary<AddressAsKey, HashSet<UInt256>> _storageSlots = [];
    // Codes touched by execution, minus those that are a currently-live in-block deploy (see
    // RecordBytecode). Captured at the world-state level for both the sandbox and main-pipeline envs.
    private readonly Dictionary<ValueHash256, byte[]> _bytecodes =
        new(GenericEqualityComparer.GetOptimized<ValueHash256>());

    // In-block code deploys (CREATE). A read of a currently-live deploy is excluded from the witness — the
    // verifier reconstructs it from the CREATE (EELS reads such codes from code_writes, not pre_state).
    // Rollback-aware via the snapshot/restore overrides: a reverted deploy is dropped so a later read of it
    // falls through to pre-state and IS captured. _deployOrder is the ordered, deduped journal; _deployFrames
    // maps each live snapshot to the deploy count when it was taken, so Restore can truncate back to it.
    private readonly HashSet<ValueHash256> _inBlockDeployed =
        new(GenericEqualityComparer.GetOptimized<ValueHash256>());
    private readonly List<ValueHash256> _deployOrder = [];
    private readonly List<(Snapshot Snapshot, int DeployCountSoFar)> _deployFrames = [];

    /// <summary>Clears the per-call witness accumulators so this instance can be reused across pooled rents.</summary>
    public void Reset()
    {
        _storageSlots.Clear();
        _bytecodes.Clear();
        _inBlockDeployed.Clear();
        _deployOrder.Clear();
        _deployFrames.Clear();
    }

    public Witness GetWitness(BlockHeader parentHeader)
    {
        CollectingSink sink = new();
        CollectStateNodes(parentHeader, sink);

        // Pool-rented buffers: any added here must also be disposed in the catch below.
        ArrayPoolList<byte[]>? codes = null;
        ArrayPoolList<byte[]>? state = null;
        ArrayPoolList<byte[]>? keys = null;
        try
        {
            codes = new ArrayPoolList<byte[]>(_bytecodes.Count);
            foreach (byte[] code in _bytecodes.Values)
                codes.Add(code);
            codes.AsSpan().Sort(Bytes.Comparer);

            state = new ArrayPoolList<byte[]>(sink.Nodes.Count);
            foreach (byte[] node in sink.Nodes.Values)
                state.Add(node);
            state.AsSpan().Sort(Bytes.Comparer);

            int totalKeysCount = _storageSlots.Count;
            foreach (KeyValuePair<AddressAsKey, HashSet<UInt256>> kvp in _storageSlots)
            {
                totalKeysCount += kvp.Value.Count;
            }

            keys = new ArrayPoolList<byte[]>(totalKeysCount);
            // Key order: <addr1><addr2><slot1-of-addr2><slot2-of-addr2><addr3><slot1-of-addr3>...
            foreach (KeyValuePair<AddressAsKey, HashSet<UInt256>> kvp in _storageSlots)
            {
                keys.Add(kvp.Key.Value.Bytes.ToArray());
                foreach (UInt256 slot in kvp.Value)
                    keys.Add(slot.ToBigEndian());
            }

            return new Witness
            {
                Codes = codes,
                State = state,
                Keys = keys,
                Headers = headerRecorder.BuildHeaders(parentHeader.Hash!, headerFinder)
            };
        }
        catch
        {
            // Return the rented buffers before propagating, else they leak.
            codes?.Dispose();
            state?.Dispose();
            keys?.Dispose();
            throw;
        }
    }

    /// <summary>Walks the pre-state trie(s) with <see cref="PatriciaTrieWitnessGenerator"/> to collect every node a stateless verifier needs.</summary>
    /// <remarks>Upsert vs Delete is decided from the committed post-state: an account that no longer exists, or a slot now zero, was removed.</remarks>
    private void CollectStateNodes(BlockHeader parentHeader, CollectingSink sink)
    {
        Hash256 stateRoot = parentHeader.StateRoot!;

        // Required for flat layout (FlatReadOnlyTrieStore resolves nothing until a scope is opened); a no-op for patricia.
        using IDisposable _ = trieStore.BeginScope(parentHeader);

        if (_storageSlots.Count > 0)
        {
            using ArrayPoolListRef<PatriciaTrieWitnessGenerator.PathEntry> accountEntries = new(_storageSlots.Count);
            foreach (AddressAsKey address in _storageSlots.Keys)
            {
                // A surviving account occupies its state-trie slot in the post-state (an upsert); a removed one is
                // a delete. Tagging survivors Upsert rather than Read keeps a newly-created account (absent in the
                // pre-state trie) from being treated as non-occupying, so a sibling deletion in the same branch does
                // not falsely collapse it — mirrors the storage-slot tagging below and the generator's
                // upsert-before-delete model.
                PatriciaTrieWitnessGenerator.AccessType access = base.AccountExists(address)
                    ? PatriciaTrieWitnessGenerator.AccessType.Upsert
                    : PatriciaTrieWitnessGenerator.AccessType.Delete;
                accountEntries.Add(new(address.Value.ToAccountPath, access));
            }
            PatriciaTrieWitnessGenerator.Generate(trieStore.GetTrieStore(null), stateRoot, accountEntries.AsSpan(), sink);

            foreach (KeyValuePair<AddressAsKey, HashSet<UInt256>> kvp in _storageSlots)
            {
                // Account touched only at the account level (e.g. self-destruct with no SLOAD): no slots to walk.
                if (kvp.Value.Count == 0) continue;
                Address address = kvp.Key;
                if (!stateReader.TryGetAccount(parentHeader, address, out AccountStruct account)) continue;
                ValueHash256 storageRoot = account.StorageRoot;
                if (storageRoot == Keccak.EmptyTreeHash.ValueHash256) continue;

                using ArrayPoolListRef<PatriciaTrieWitnessGenerator.PathEntry> slotEntries = new(kvp.Value.Count);
                foreach (UInt256 slot in kvp.Value)
                {
                    ValueHash256 slotKey = default;
                    StorageTree.ComputeKeyWithLookup(slot, ref slotKey);
                    // A non-zero post-state slot is occupied (an upsert); a zero one was removed (a delete). The
                    // generator replays upserts before deletes, so a delete+insert block does not over-capture the
                    // branch's collapse sibling.
                    bool deleted = base.Get(new StorageCell(address, slot)).IndexOfAnyExcept((byte)0) < 0;
                    slotEntries.Add(new(slotKey, deleted ? PatriciaTrieWitnessGenerator.AccessType.Delete : PatriciaTrieWitnessGenerator.AccessType.Upsert));
                }
                PatriciaTrieWitnessGenerator.Generate(trieStore.GetTrieStore(address), new Hash256(storageRoot), slotEntries.AsSpan(), sink);
            }
        }

        // Nothing touched but a non-empty state root: anchor the witness with the root node.
        if (sink.Nodes.Count == 0 && stateRoot != Keccak.EmptyTreeHash)
        {
            IScopedTrieStore stateResolver = trieStore.GetTrieStore(null);
            TreePath path = TreePath.Empty;
            TrieNode root = stateResolver.FindCachedOrUnknown(path, stateRoot);
            root.ResolveNode(stateResolver, path);
            if (root.Keccak is not null) sink.Add(path, root);
        }
    }

    // Not thread-safe (plain dictionary), so the generator is always invoked with parallelize off.
    private sealed class CollectingSink : PatriciaTrieWitnessGenerator.ISink
    {
        public Dictionary<Hash256AsKey, byte[]> Nodes { get; } = [];

        public void Add(in TreePath path, TrieNode node) => Nodes[node.Keccak!] = node.FullRlp.ToArray();
    }

    public override bool TryGetAccount(Address address, out AccountStruct account)
    {
        RecordEmptySlots(address);
        return base.TryGetAccount(address, out account);
    }

    public override ulong GetNonce(Address address)
    {
        RecordEmptySlots(address);
        return base.GetNonce(address);
    }

    public override bool IsStorageEmpty(Address address)
    {
        RecordEmptySlots(address);
        return base.IsStorageEmpty(address);
    }

    public override byte[]? GetCode(Address address)
    {
        RecordEmptySlots(address);
        byte[]? code = base.GetCode(address);
        RecordBytecode(code);
        return code;
    }

    public override byte[]? GetCode(in ValueHash256 codeHash)
    {
        byte[]? code = base.GetCode(in codeHash);
        // Hash already known: skip re-Keccaking the (potentially large) bytecode
        RecordBytecode(in codeHash, code);
        return code;
    }

    /// <inheritdoc/>
    /// <remarks>The code-DB wrapper captures the bytecode if this read reaches the pre-state DB.</remarks>
    public override void RecordAccountAccess(Address address) => RecordEmptySlots(address);

    public override void RecordBytecodeAccess(Address address) => GetCode(address);

    public override bool IsContract(Address address)
    {
        RecordEmptySlots(address);
        return base.IsContract(address);
    }

    public override bool AccountExists(Address address)
    {
        RecordEmptySlots(address);
        return base.AccountExists(address);
    }

    public override bool IsDeadAccount(Address address)
    {
        RecordEmptySlots(address);
        return base.IsDeadAccount(address);
    }

    public override ref readonly UInt256 GetBalance(Address address)
    {
        RecordEmptySlots(address);
        return ref base.GetBalance(address);
    }

    public override ref readonly ValueHash256 GetCodeHash(Address address)
    {
        RecordEmptySlots(address);
        return ref base.GetCodeHash(address);
    }

    public override ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell)
    {
        RecordSlot(storageCell);
        return base.GetOriginal(in storageCell);
    }

    public override ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        RecordSlot(storageCell);
        return base.Get(in storageCell);
    }

    public override void Set(in StorageCell storageCell, byte[] newValue)
    {
        RecordSlot(storageCell);
        base.Set(in storageCell, newValue);
    }

    public override void ClearStorage(Address address)
    {
        RecordEmptySlots(address);
        base.ClearStorage(address);
    }

    public override void DeleteAccount(Address address)
    {
        RecordEmptySlots(address);
        base.DeleteAccount(address);
    }

    public override void CreateAccount(Address address, in UInt256 balance, in ulong nonce = default)
    {
        RecordEmptySlots(address);
        base.CreateAccount(address, in balance, in nonce);
    }

    public override void CreateAccountIfNotExists(Address address, in UInt256 balance, in ulong nonce = default)
    {
        RecordEmptySlots(address);
        base.CreateAccountIfNotExists(address, in balance, in nonce);
    }

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        RecordEmptySlots(address);
        // Track the deploy so a later read of its code is excluded from the witness (the verifier replays the
        // CREATE and regenerates it). Deduped by hash so a redeploy of an already-live code is not relogged,
        // mirroring EELS code_writes (keyed by hash). The deployed code itself is never added to _bytecodes.
        if (_inBlockDeployed.Add(codeHash)) _deployOrder.Add(codeHash);
        return base.InsertCode(address, in codeHash, code, spec, isGenesis);
    }

    public override Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        // A new transaction commits the previous one's deploys (they can no longer be reverted to a point
        // before this transaction), so drop its frames — bounding the stack to one transaction's call depth.
        if (newTransactionStart) _deployFrames.Clear();
        Snapshot snapshot = base.TakeSnapshot(newTransactionStart);
        _deployFrames.Add((snapshot, _deployOrder.Count));
        return snapshot;
    }

    public override void Restore(Snapshot snapshot)
    {
        // Unwind to the frame that took `snapshot` and drop deploys made after it, so a reverted CREATE's
        // code falls back to pre-state and is captured (rollback-aware code_writes). Every TakeSnapshot pushes
        // a frame, so the match exists; default to keeping all deploys if it somehow doesn't.
        int keep = _deployOrder.Count;
        for (int i = _deployFrames.Count - 1; i >= 0; i--)
        {
            if (SameSnapshot(_deployFrames[i].Snapshot, snapshot))
            {
                keep = _deployFrames[i].DeployCountSoFar;
                CollectionsMarshal.SetCount(_deployFrames, i + 1);
                break;
            }
        }
        for (int i = _deployOrder.Count - 1; i >= keep; i--)
            _inBlockDeployed.Remove(_deployOrder[i]);
        CollectionsMarshal.SetCount(_deployOrder, keep);

        base.Restore(snapshot);
    }

    private static bool SameSnapshot(in Snapshot a, in Snapshot b)
        => a.StateSnapshot == b.StateSnapshot
           && a.StorageSnapshot.PersistentStorageSnapshot == b.StorageSnapshot.PersistentStorageSnapshot
           && a.StorageSnapshot.TransientStorageSnapshot == b.StorageSnapshot.TransientStorageSnapshot;

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        RecordEmptySlots(address);
        base.AddToBalance(address, in balanceChange, spec, out oldBalance);
    }

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        RecordEmptySlots(address);
        return base.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec, out oldBalance);
    }

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        RecordEmptySlots(address);
        base.SubtractFromBalance(address, in balanceChange, spec, out oldBalance);
    }

    public override void IncrementNonce(Address address, ulong delta, out ulong oldNonce)
    {
        RecordEmptySlots(address);
        base.IncrementNonce(address, delta, out oldNonce);
    }

    public override void DecrementNonce(Address address, ulong delta)
    {
        RecordEmptySlots(address);
        base.DecrementNonce(address, delta);
    }

    public override void SetNonce(Address address, in ulong nonce)
    {
        RecordEmptySlots(address);
        base.SetNonce(address, in nonce);
    }

    public override void CreateEmptyAccountIfDeleted(Address address)
    {
        RecordEmptySlots(address);
        base.CreateEmptyAccountIfDeleted(address);
    }

    private void RecordSlot(in StorageCell storageCell)
        => RecordEmptySlots(storageCell.Address).Add(storageCell.Index);

    private HashSet<UInt256> RecordEmptySlots(Address address)
    {
        ref HashSet<UInt256>? slots = ref CollectionsMarshal.GetValueRefOrAddDefault(
            _storageSlots, address, out _);
        slots ??= [];
        return slots;
    }

    private void RecordBytecode(byte[]? code)
    {
        // Address-keyed paths don't carry the code hash, so compute it here.
        if (code?.Length > 0)
            RecordBytecode(ValueKeccak.Compute(code), code);
    }

    private void RecordBytecode(in ValueHash256 codeHash, byte[]? code)
    {
        // Skip empty code and currently-live in-block deploys (see _inBlockDeployed): EELS get_code's read
        // chain (tx/block code_writes → pre_state) serves those from code_writes, not pre_state.
        if (code is not { Length: > 0 }) return;
        if (!_inBlockDeployed.Contains(codeHash))
            _bytecodes.TryAdd(codeHash, code);
    }
}
