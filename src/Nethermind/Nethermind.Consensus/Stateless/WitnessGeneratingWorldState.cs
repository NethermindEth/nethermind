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
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

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
            // State-trie capture: one MultiAccountProofCollector walk captures the state-trie nodes
            // along the path to every touched account in a single pass. Each per-account
            // AccountProofCollector walk below then re-traverses the state-trie path to reach the
            // account leaf and capture its storage root — so the total state-trie traversal count
            // is 1 (capture) + N (per-account re-walks). The dedup in the downstream PooledSet
            // means the *output* is the same as a single-pass walk; the saving is in avoiding N
            // separate capture passes (and N×state-trie-node RLP encodings) for shared-path nodes.
            // TODO: teach AccountProofCollector to take the storage root from a previously-visited
            // account node so the state-trie path can be walked exactly once.
            MultiAccountProofCollector stateCollector = new(_storageSlots);
            using (trieStore.PauseRecording())
            {
                stateReader.RunTreeVisitor(stateCollector, parentHeader);
            }
            foreach (byte[] node in stateCollector.Nodes)
            {
                stateNodes.Add(node);
            }

            foreach (KeyValuePair<AddressAsKey, HashSet<UInt256>> entry in _storageSlots)
            {
                if (entry.Value.Count == 0) continue;
                AccountProofCollector storageCollector = new(entry.Key.Value, entry.Value);
                using (trieStore.PauseRecording())
                {
                    stateReader.RunTreeVisitor(storageCollector, parentHeader);
                }
                (_, IReadOnlyList<byte[]>[] storageProofs) = storageCollector.GetRawResult();
                foreach (IReadOnlyList<byte[]> slotProof in storageProofs)
                {
                    foreach (byte[] node in slotProof)
                    {
                        stateNodes.Add(node);
                    }
                }
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

            int totalKeysCount = 0;
            foreach (KeyValuePair<AddressAsKey, HashSet<UInt256>> kvp in _storageSlots)
            {
                totalKeysCount++;
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
        // Both the codeHash and the code bytes are available here. A CREATE/CREATE2 inside the
        // proof_call that deploys a contract without a subsequent CALL to it would otherwise leave
        // the witness without that contract's bytecode — the verifier reconstructs the account's
        // codeHash from the state proof, looks it up in witness.Codes, and finds nothing.
        // Skip the genesis path: the genesis block's contracts are referenced from chain spec,
        // not from the state proof, and the runtime code is already in the chain's code DB.
        if (!isGenesis && code.Length > 0)
            _bytecodes.TryAdd(codeHash, code.ToArray());
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
