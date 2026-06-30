// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Collections.Pooled;
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

/// <param name="stateReader">Serves the post-execution proof-collection walks in <see cref="GetWitness"/>;
/// must be a plain (non-capturing) reader — re-traversal is proof collection, not state access, so
/// recording it into <paramref name="trieStore"/> would only duplicate the witness buffers.</param>
public class WitnessGeneratingWorldState(
    IWorldState state,
    IStateReader stateReader,
    WitnessCapturingTrieStore trieStore,
    WitnessGeneratingHeaderFinder headerFinder)
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
        // A reverted write leaves no trie traversal (the write was cached, then discarded), so its
        // trie nodes were never captured. The walk below re-traverses the touched keys to capture
        // them — only needed for cross-client (e.g. geth) stateless re-execution; our own execution
        // wouldn't require it.
        if (!trieStore.TouchedNodesRlp.Any())
        {
            // No storage-slot or account reads — lazy TrieNode handling can leave the root node
            // uncaptured. Resolve it explicitly so the witness still includes it.
            ITrieNodeResolver stateResolver = trieStore.GetTrieStore(null);
            TreePath path = TreePath.Empty;
            TrieNode node = stateResolver.FindCachedOrUnknown(path, parentHeader.StateRoot!);
            node.ResolveNode(stateResolver, path);
        }

        using PooledSet<byte[]> stateNodes = new(trieStore.TouchedNodesRlp, Bytes.EqualityComparer);
        if (_storageSlots.Count > 0)
        {
            // A single walk captures both the state-trie path to every touched account and, via the
            // storage descent at each account leaf, the storage-trie path to every touched slot.
            MultiAccountProofCollector collector = new(_storageSlots);
            stateReader.RunTreeVisitor(collector, parentHeader);
            foreach (byte[] node in collector.Nodes)
            {
                stateNodes.Add(node);
            }
        }

        // New pool-rented buffers added here must also be disposed in the catch below.
        ArrayPoolList<byte[]>? codes = null;
        ArrayPoolList<byte[]>? state = null;
        ArrayPoolList<byte[]>? keys = null;
        try
        {
            codes = new ArrayPoolList<byte[]>(_bytecodes.Count);
            foreach (byte[] code in _bytecodes.Values)
                codes.Add(code);

            state = new ArrayPoolList<byte[]>(stateNodes.Count);
            foreach (byte[] node in stateNodes)
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
                Headers = headerFinder.GetWitnessHeaders(parentHeader.Hash!)
            };
        }
        catch
        {
            // Any failure mid-build returns the rented buffers before propagating, else they leak:
            // an OOM while filling a list, or GetWitnessHeaders throwing because a walked ancestor
            // header vanished (reorg/prune between the call and the witness build).
            codes?.Dispose();
            state?.Dispose();
            keys?.Dispose();
            throw;
        }
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
