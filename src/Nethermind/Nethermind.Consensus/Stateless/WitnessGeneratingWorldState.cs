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
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Stateless;

public class WitnessGeneratingWorldState(IWorldState inner, IStateReader stateReader, WitnessCapturingTrieStore trieStore, WitnessGeneratingHeaderFinder headerFinder) : IWorldState
{
    private readonly Dictionary<Address, HashSet<UInt256>> _storageSlots = new();

    private readonly Dictionary<ValueHash256, byte[]> _bytecodes = new();

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

    public bool HasStateForBlock(BlockHeader? baseBlock) => inner.HasStateForBlock(baseBlock);

    public void Restore(Snapshot snapshot) => inner.Restore(snapshot);

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        RecordEmptySlots(address);
        return ((IWorldState)inner).TryGetAccount(address, out account);
    }

    public Account GetAccount(Address address)
    {
        RecordEmptySlots(address);
        return inner.GetAccount(address);
    }

    public Hash256 StateRoot => inner.StateRoot;

    public bool IsInScope => inner.IsInScope;

    public IWorldStateScopeProvider ScopeProvider => inner.ScopeProvider;

    public byte[]? GetCode(Address address)
    {
        RecordEmptySlots(address);
        byte[] code = inner.GetCode(address);
        RecordBytecode(code);
        return code;
    }

    public byte[]? GetCode(in ValueHash256 codeHash)
    {
        byte[] code = inner.GetCode(in codeHash);
        RecordBytecode(code);
        return code;
    }

    public bool IsContract(Address address)
    {
        RecordEmptySlots(address);
        return inner.IsContract(address);
    }

    public bool AccountExists(Address address)
    {
        RecordEmptySlots(address);
        return inner.AccountExists(address);
    }

    public bool IsDeadAccount(Address address)
    {
        RecordEmptySlots(address);
        return inner.IsDeadAccount(address);
    }

    public ref readonly UInt256 GetBalance(Address address)
    {
        RecordEmptySlots(address);
        return ref inner.GetBalance(address);
    }

    public ref readonly ValueHash256 GetCodeHash(Address address)
    {
        RecordEmptySlots(address);
        return ref inner.GetCodeHash(address);
    }

    public byte[] GetOriginal(in StorageCell storageCell)
    {
        RecordSlot(storageCell);
        return inner.GetOriginal(in storageCell);
    }

    public ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        RecordSlot(storageCell);
        return inner.Get(in storageCell);
    }

    public void Set(in StorageCell storageCell, byte[] newValue)
    {
        RecordSlot(storageCell);
        inner.Set(in storageCell, newValue);
    }

    // Transient state does not need trie node capture as it's purely in-memory storage, no trie representation whatsoever
    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
        => inner.GetTransientState(in storageCell);

    // Transient state does not need trie node capture as it's purely in-memory storage, no trie representation whatsoever
    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
        => inner.SetTransientState(in storageCell, newValue);

    public void Reset(bool resetBlockChanges = true) => inner.Reset(resetBlockChanges);

    public Snapshot TakeSnapshot(bool newTransactionStart = false) => inner.TakeSnapshot(newTransactionStart);

    public void WarmUp(AccessList? accessList) => inner.WarmUp(accessList);

    public void WarmUp(Address address) => inner.WarmUp(address);

    public void ClearStorage(Address address)
    {
        RecordEmptySlots(address);
        inner.ClearStorage(address);
    }

    public void RecalculateStateRoot() => inner.RecalculateStateRoot();

    public void DeleteAccount(Address address)
    {
        RecordEmptySlots(address);
        inner.DeleteAccount(address);
    }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        RecordEmptySlots(address);
        inner.CreateAccount(address, in balance, in nonce);
    }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        RecordEmptySlots(address);
        inner.CreateAccountIfNotExists(address, in balance, in nonce);
    }

    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        RecordEmptySlots(address);
        return inner.InsertCode(address, in codeHash, code, spec, isGenesis);
    }

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        RecordEmptySlots(address);
        inner.AddToBalance(address, in balanceChange, spec);
    }

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, bool incrementNonce = false)
    {
        RecordEmptySlots(address);
        return inner.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec, incrementNonce);
    }

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        RecordEmptySlots(address);
        inner.SubtractFromBalance(address, in balanceChange, spec);
    }

    public void IncrementNonce(Address address, UInt256 delta)
    {
        RecordEmptySlots(address);
        inner.IncrementNonce(address, delta);
    }

    public void DecrementNonce(Address address, UInt256 delta)
    {
        RecordEmptySlots(address);
        inner.DecrementNonce(address, delta);
    }

    public void SetNonce(Address address, in UInt256 nonce)
    {
        RecordEmptySlots(address);
        inner.SetNonce(address, nonce);
    }

    public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true) =>
        inner.Commit(releaseSpec, isGenesis, commitRoots);

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true, Address? retainInCache = null) =>
        inner.Commit(releaseSpec, tracer, isGenesis, commitRoots, retainInCache);

    public void CommitTree(long blockNumber) => inner.CommitTree(blockNumber);

    public ArrayPoolList<AddressAsKey>? GetAccountChanges() => inner.GetAccountChanges();

    public void ResetTransient() => inner.ResetTransient();

    public IDisposable BeginScope(BlockHeader? baseBlock) => inner.BeginScope(baseBlock);

    public void CreateEmptyAccountIfDeleted(Address address)
    {
        RecordEmptySlots(address);
        inner.CreateEmptyAccountIfDeleted(address);
    }

    private void RecordSlot(in StorageCell storageCell) => RecordEmptySlots(storageCell.Address).Add(storageCell.Index);

    private HashSet<UInt256> RecordEmptySlots(Address address)
    {
        ref HashSet<UInt256>? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_storageSlots, address, out _);
        slot ??= new HashSet<UInt256>();
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
