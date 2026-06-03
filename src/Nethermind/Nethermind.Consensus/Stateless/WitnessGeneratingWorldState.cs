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

public class WitnessGeneratingWorldState(IWorldState state, IStateReader stateReader, WitnessCapturingTrieStore trieStore, WitnessGeneratingHeaderFinder headerFinder)
    : WorldStateDecorator(state)
{
    private readonly Dictionary<Address, HashSet<UInt256>> _storageSlots = [];

    private readonly Dictionary<ValueHash256, byte[]> _bytecodes = new(GenericEqualityComparer.GetOptimized<ValueHash256>());

    public Witness GetWitness(BlockHeader parentHeader)
    {
        // Build state nodes
        //
        // The purpose of adding this tree visitor over the captured keys is for capturing trie nodes
        // for slots that were never read and yet written to (writes are cached) but transaction reverted.
        // Transaction reverting implies that cached writes got discarded and trie never got traversed
        // for those keys, hence the associated trie nodes never got captured.
        //
        // We could potentially enforce read-before-write for every function called within this file,
        // but this tree visitor solution is safer, more defensive and maintainable.
        //
        // Notes:
        // - We wouldn't need to capture those trie nodes for nethermind stateless execution, but we need to
        // if we want to be compatible with other clients (such as geth, for example) so that our witness
        // can be used for their stateless execution.
        // - Trie nodes captured using this additional tree visitor pattern should not add unnecessary trie nodes
        // as anyway all keys recorded in this file should either be read or written to. In both cases, we want
        // trie traversal with trie nodes capture along the path to be compatible with other clients.
        //

        if (!trieStore.TouchedNodesRlp.Any())
        {
            // When there are no storage-slot or account reads, lazy TrieNode handling can leave the root node
            // unrecorded, especially when recording is skipped for nodes with an unknown type.
            // To ensure the witness still includes the root node in this case, we explicitly resolve it here.
            // This usually works because trie nodes, and especially the root node, tend to be cached.
            ITrieNodeResolver stateResolver = trieStore.GetTrieStore(null);
            TreePath path = TreePath.Empty;
            TrieNode node = stateResolver.FindCachedOrUnknown(path, parentHeader.StateRoot!);
            node.ResolveNode(stateResolver, path);
        }

        using PooledSet<byte[]> stateNodes = new(trieStore.TouchedNodesRlp, Bytes.EqualityComparer);
        foreach ((Address account, HashSet<UInt256> slots) in _storageSlots)
        {
            AccountProofCollector accountProofCollector = new(account, slots);
            stateReader.RunTreeVisitor(accountProofCollector, parentHeader);
            (IReadOnlyList<byte[]> accountProof, IReadOnlyList<byte[]>[] storageProof) = accountProofCollector.GetRawResult();
            stateNodes.AddRange(accountProof);
            stateNodes.AddRange(storageProof.SelectMany(p => p));
        }

        ArrayPoolList<byte[]> codes = new(_bytecodes.Count);
        foreach (byte[] code in _bytecodes.Values)
            codes.Add(code);

        ArrayPoolList<byte[]> state = new(stateNodes.Count);
        foreach (byte[] node in stateNodes)
            state.Add(node);

        // Build keys
        int totalKeysCount = 0;
        foreach (KeyValuePair<Address, HashSet<UInt256>> kvp in _storageSlots)
        {
            totalKeysCount++;
            totalKeysCount += kvp.Value.Count;
        }

        ArrayPoolList<byte[]> keys = new(totalKeysCount);

        // Keys should be ordered like: <address1><address2><slot1-address2><slot2-address2><address3><slot1-address3>
        foreach (KeyValuePair<Address, HashSet<UInt256>> kvp in _storageSlots)
        {
            keys.Add(kvp.Key.Bytes.ToArray());
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

    public override bool TryGetAccount(Address address, out AccountStruct account)
    {
        RecordEmptySlots(address);
        return State.TryGetAccount(address, out account);
    }

    public override UInt256 GetNonce(Address address)
    {
        RecordEmptySlots(address);
        return State.GetNonce(address);
    }

    public override bool IsStorageEmpty(Address address)
    {
        RecordEmptySlots(address);
        return State.IsStorageEmpty(address);
    }

    public override byte[]? GetCode(Address address)
    {
        RecordEmptySlots(address);
        byte[] code = State.GetCode(address);
        RecordBytecode(code);
        return code;
    }

    public override byte[]? GetCode(in ValueHash256 codeHash)
    {
        byte[] code = State.GetCode(in codeHash);
        RecordBytecode(code);
        return code;
    }

    public override void RecordBytecodeAccess(Address address) => GetCode(address);

    public override bool IsContract(Address address)
    {
        RecordEmptySlots(address);
        return State.IsContract(address);
    }

    public override bool AccountExists(Address address)
    {
        RecordEmptySlots(address);
        return State.AccountExists(address);
    }

    public override bool IsDeadAccount(Address address)
    {
        RecordEmptySlots(address);
        return State.IsDeadAccount(address);
    }

    public override ref readonly UInt256 GetBalance(Address address)
    {
        RecordEmptySlots(address);
        return ref State.GetBalance(address);
    }

    public override ref readonly ValueHash256 GetCodeHash(Address address)
    {
        RecordEmptySlots(address);
        return ref State.GetCodeHash(address);
    }

    public override ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell)
    {
        RecordSlot(storageCell);
        return State.GetOriginal(in storageCell);
    }

    public override ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        RecordSlot(storageCell);
        return State.Get(in storageCell);
    }

    public override void Set(in StorageCell storageCell, byte[] newValue)
    {
        RecordSlot(storageCell);
        State.Set(in storageCell, newValue);
    }

    public override void ClearStorage(Address address)
    {
        RecordEmptySlots(address);
        State.ClearStorage(address);
    }

    public override void DeleteAccount(Address address)
    {
        RecordEmptySlots(address);
        State.DeleteAccount(address);
    }

    public override void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        RecordEmptySlots(address);
        State.CreateAccount(address, in balance, in nonce);
    }

    public override void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        RecordEmptySlots(address);
        State.CreateAccountIfNotExists(address, in balance, in nonce);
    }

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        RecordEmptySlots(address);
        return State.InsertCode(address, in codeHash, code, spec, isGenesis);
    }

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        RecordEmptySlots(address);
        State.AddToBalance(address, in balanceChange, spec, out oldBalance);
    }

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        RecordEmptySlots(address);
        return State.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec, out oldBalance);
    }

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        RecordEmptySlots(address);
        State.SubtractFromBalance(address, in balanceChange, spec, out oldBalance);
    }

    public override void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce)
    {
        RecordEmptySlots(address);
        State.IncrementNonce(address, delta, out oldNonce);
    }

    public override void DecrementNonce(Address address, UInt256 delta)
    {
        RecordEmptySlots(address);
        State.DecrementNonce(address, delta);
    }

    public override void SetNonce(Address address, in UInt256 nonce)
    {
        RecordEmptySlots(address);
        State.SetNonce(address, in nonce);
    }

    public override void CreateEmptyAccountIfDeleted(Address address)
    {
        RecordEmptySlots(address);
        State.CreateEmptyAccountIfDeleted(address);
    }

    private void RecordSlot(in StorageCell storageCell) => RecordEmptySlots(storageCell.Address).Add(storageCell.Index);

    private HashSet<UInt256> RecordEmptySlots(Address address)
    {
        ref HashSet<UInt256>? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_storageSlots, address, out _);
        slot ??= [];
        return slot;
    }

    private void RecordBytecode(byte[]? code)
    {
        // Unnecessary to record empty code
        if (code?.Length > 0)
        {
            Hash256 codeHash = Keccak.Compute(code);
            _bytecodes.TryAdd(codeHash, code);
        }
    }
}
