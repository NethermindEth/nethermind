// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

/// <param name="stateReader">Plain (non-capturing) reader for pre-state accounts (their storage roots) at
/// the parent block, used to drive the post-execution witness walk in <see cref="GetWitness"/>.</param>
/// <param name="trieStore">Read-only trie store walked at the pre-state root to collect the witness nodes.</param>
public class WitnessGeneratingWorldState(
    IWorldState state,
    IStateReader stateReader,
    IReadOnlyTrieStore trieStore,
    WitnessHeaderRecorder headerRecorder,
    IHeaderFinder headerFinder)
    : WorldStateDecorator(state)
{
    private readonly Dictionary<AddressAsKey, HashSet<UInt256>> _storageSlots = [];
    private readonly Dictionary<ValueHash256, byte[]> _bytecodes =
        new(GenericEqualityComparer.GetOptimized<ValueHash256>());

    /// <summary>Clears the per-call witness accumulators so this instance can be reused across pooled rents.</summary>
    public void Reset()
    {
        _storageSlots.Clear();
        _bytecodes.Clear();
    }

    public Witness GetWitness(BlockHeader parentHeader)
    {
        CollectingSink sink = new();
        CollectStateNodes(parentHeader, sink);

        // New pool-rented buffers added here must also be disposed in the catch below.
        ArrayPoolList<byte[]>? codes = null;
        ArrayPoolList<byte[]>? state = null;
        ArrayPoolList<byte[]>? keys = null;
        try
        {
            codes = new ArrayPoolList<byte[]>(_bytecodes.Count);
            foreach (byte[] code in _bytecodes.Values)
                codes.Add(code);

            state = new ArrayPoolList<byte[]>(sink.Nodes.Count);
            foreach (byte[] node in sink.Nodes.Values)
                state.Add(node);

            int totalKeysCount = _storageSlots.Count;
            foreach (KeyValuePair<AddressAsKey, HashSet<UInt256>> kvp in _storageSlots)
            {
                totalKeysCount += kvp.Value.Count;
            }

            keys = new ArrayPoolList<byte[]>(totalKeysCount);
            // Keys ordered like: <addr1><addr2><slot1-of-addr2><slot2-of-addr2><addr3><slot1-of-addr3>
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
            // Any failure mid-build returns the rented buffers before propagating, else they leak:
            // an OOM while filling a list, or BuildHeaders throwing because a walked ancestor
            // header vanished (reorg/prune between the call and the witness build).
            codes?.Dispose();
            state?.Dispose();
            keys?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Walks the pre-state trie(s) with <see cref="PatriciaTrieWitnessGenerator"/> to collect every node a
    /// stateless verifier needs: one pass over the state trie for the touched accounts, then one pass per
    /// account over its pre-state storage trie for the touched slots. Read/Delete is read off the committed
    /// post-state (an account that no longer exists, or a slot whose value is now zero, was removed).
    /// </summary>
    private void CollectStateNodes(BlockHeader parentHeader, CollectingSink sink)
    {
        Hash256 stateRoot = parentHeader.StateRoot!;

        // Flat's IReadOnlyTrieStore (FlatReadOnlyTrieStore) resolves nothing until a scope is opened:
        // BeginScope gathers the read-only snapshot bundle for the parent (blockNumber, stateRoot).
        // On patricia BeginScope is a no-op, so this is required for flat and harmless for half-path.
        using IDisposable _ = trieStore.BeginScope(parentHeader);

        if (_storageSlots.Count > 0)
        {
            using ArrayPoolList<PatriciaTrieWitnessGenerator.PathEntry> accountEntries = new(_storageSlots.Count);
            foreach (AddressAsKey address in _storageSlots.Keys)
            {
                PatriciaTrieWitnessGenerator.AccessType access = base.AccountExists(address)
                    ? PatriciaTrieWitnessGenerator.AccessType.Read
                    : PatriciaTrieWitnessGenerator.AccessType.Delete;
                accountEntries.Add(new(address.Value.ToAccountPath, access));
            }
            PatriciaTrieWitnessGenerator.Generate(trieStore.GetTrieStore(null), stateRoot, accountEntries.AsSpan(), sink);

            foreach (KeyValuePair<AddressAsKey, HashSet<UInt256>> kvp in _storageSlots)
            {
                // An account touched only at the account level (e.g. a self-destruct with no SLOAD) has no
                // slots to walk; removing its state-trie leaf already accounts for its whole storage subtree.
                if (kvp.Value.Count == 0) continue;
                Address address = kvp.Key;
                if (!stateReader.TryGetAccount(parentHeader, address, out AccountStruct account)) continue;
                ValueHash256 storageRoot = account.StorageRoot;
                if (storageRoot == Keccak.EmptyTreeHash.ValueHash256) continue;

                using ArrayPoolList<PatriciaTrieWitnessGenerator.PathEntry> slotEntries = new(kvp.Value.Count);
                foreach (UInt256 slot in kvp.Value)
                {
                    ValueHash256 slotKey = default;
                    StorageTree.ComputeKeyWithLookup(slot, ref slotKey);
                    bool deleted = base.Get(new StorageCell(address, slot)).IndexOfAnyExcept((byte)0) < 0;
                    slotEntries.Add(new(slotKey, deleted ? PatriciaTrieWitnessGenerator.AccessType.Delete : PatriciaTrieWitnessGenerator.AccessType.Read));
                }
                PatriciaTrieWitnessGenerator.Generate(trieStore.GetTrieStore(address), new Hash256(storageRoot), slotEntries.AsSpan(), sink);
            }
        }

        // Nothing touched but a non-empty state root: anchor the witness with the root node, which lazy
        // TrieNode handling can otherwise leave uncollected.
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

    public override UInt256 GetNonce(Address address)
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
        // The hash is already known here, so skip re-Keccaking the (potentially large) bytecode —
        // DELEGATECALL loops to the same contract would otherwise pay it on every read.
        RecordBytecode(in codeHash, code);
        return code;
    }

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

    public override void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        RecordEmptySlots(address);
        base.CreateAccount(address, in balance, in nonce);
    }

    public override void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        RecordEmptySlots(address);
        base.CreateAccountIfNotExists(address, in balance, in nonce);
    }

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        RecordEmptySlots(address);
        // Deployed code is deliberately NOT captured: a stateless re-execution replays the CREATE
        // and regenerates it, and EEST stateless tests assert it is absent from the witness.
        return base.InsertCode(address, in codeHash, code, spec, isGenesis);
    }

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

    public override void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce)
    {
        RecordEmptySlots(address);
        base.IncrementNonce(address, delta, out oldNonce);
    }

    public override void DecrementNonce(Address address, UInt256 delta)
    {
        RecordEmptySlots(address);
        base.DecrementNonce(address, delta);
    }

    public override void SetNonce(Address address, in UInt256 nonce)
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
        // The Address-keyed paths don't carry the code hash, so compute it here.
        if (code?.Length > 0)
            RecordBytecode(ValueKeccak.Compute(code), code);
    }

    private void RecordBytecode(in ValueHash256 codeHash, byte[]? code)
    {
        // Unnecessary to record empty code
        if (code?.Length > 0)
            _bytecodes.TryAdd(codeHash, code);
    }
}
