// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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

public class WitnessGeneratingWorldState(WorldState inner, IStateReader stateReader, WitnessCapturingTrieStore trieStore, WitnessGeneratingHeaderFinder headerFinder) : IWorldState
{
    private readonly Dictionary<Address, HashSet<UInt256>> _storageSlots = new();

    private readonly Dictionary<ValueHash256, byte[]> _bytecodes = new();

    public void RecordBytecode(byte[] code)
    {
        // Unnecessary to record empty code
        if (code.Length != 0)
        {
            Hash256 codeHash = Keccak.Compute(code);
            _bytecodes.TryAdd(codeHash, code);
        }
    }

    public Witness GetWitness(BlockHeader parentHeader)
    {

        // Build state nodes
        //
        // The purpose of adding this tree visitor over the captured keys is for capturing trie nodes that
        // were modified but reset to their original value within a block processing. Otherwise, the
        // WitnessCapturingTrie only would not capture these nodes and they would not be included in the witness.
        // For example, these nodes are captured in geth. But this solution might capture additional nodes not
        // necessarily needed for the witness. There might be a better solution, it is just not the priority now.
        HashSet<byte[]> stateNodes = new(Bytes.EqualityComparer);
        stateNodes.UnionWith(trieStore.TouchedNodesRlp);
        foreach ((Address account, HashSet<UInt256> slots) in _storageSlots)
        {
            AccountProofCollector accountProofCollector = new(account, slots.ToArray());
            stateReader.RunTreeVisitor(accountProofCollector, parentHeader);
            AccountProof accountProof = accountProofCollector.BuildResult();

            stateNodes.AddRange(accountProof.Proof);
            stateNodes.AddRange(accountProof.StorageProofs.SelectMany(storageProof => storageProof.Proof));
        }

        // Build keys
        int totalKeysCount = 0;
        foreach (var kvp in _storageSlots)
        {
            totalKeysCount++;
            totalKeysCount += kvp.Value.Count;
        }

        byte[][] keys = new byte[totalKeysCount][];
        int i = 0;

        // Keys should be ordered like: <address1><address2><slot1-address2><slot2-address2><address3><slot1-address3>
        foreach (var kvp in _storageSlots)
        {
            keys[i++] = kvp.Key.Bytes;
            foreach (var slot in kvp.Value)
                keys[i++] = slot.ToBigEndian();
        }

        return new Witness()
        {
            Codes = _bytecodes.Values.ToArray(),
            State = stateNodes.ToArray(),
            Keys = keys,
            Headers = headerFinder.GetWitnessHeaders(parentHeader.Hash)
        };
    }

    public bool HasStateForBlock(BlockHeader? baseBlock) => inner.HasStateForBlock(baseBlock);

    public void Restore(Snapshot snapshot)
    {
        inner.Restore(snapshot);
    }

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        _storageSlots.TryAdd(address, []);
        return ((IWorldState)inner).TryGetAccount(address, out account);
    }

    public Hash256 StateRoot { get => inner.StateRoot; }

    public bool IsInScope => inner.IsInScope;

    public IWorldStateScopeProvider ScopeProvider => inner.ScopeProvider;

    public byte[] GetCode(Address address)
    {
        _storageSlots.TryAdd(address, []);
        byte[] code = inner.GetCode(address);
        RecordBytecode(code);
        return code;
    }

    public byte[] GetCode(in ValueHash256 codeHash)
    {
        byte[] code = inner.GetCode(in codeHash);
        RecordBytecode(code);
        return code;
    }

    public bool IsContract(Address address)
    {
        _storageSlots.TryAdd(address, []);
        return inner.IsContract(address);
    }

    public bool AccountExists(Address address)
    {
        _storageSlots.TryAdd(address, []);
        return inner.AccountExists(address);
    }

    public bool IsDeadAccount(Address address)
    {
        _storageSlots.TryAdd(address, []);
        return inner.IsDeadAccount(address);
    }

    public ref readonly UInt256 GetBalance(Address address)
    {
        _storageSlots.TryAdd(address, []);
        return ref inner.GetBalance(address);
    }

    public ref readonly ValueHash256 GetCodeHash(Address address)
    {
        _storageSlots.TryAdd(address, []);
        return ref inner.GetCodeHash(address);
    }

    public byte[] GetOriginal(in StorageCell storageCell)
    {
        if (_storageSlots.ContainsKey(storageCell.Address))
        {
            _storageSlots[storageCell.Address].Add(storageCell.Index);
        }
        else
        {
            _storageSlots.Add(storageCell.Address, new HashSet<UInt256> { storageCell.Index });
        }

        return inner.GetOriginal(in storageCell);
    }

    public ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        if (_storageSlots.ContainsKey(storageCell.Address))
        {
            _storageSlots[storageCell.Address].Add(storageCell.Index);
        }
        else
        {
            _storageSlots.Add(storageCell.Address, new HashSet<UInt256> { storageCell.Index });
        }
        return inner.Get(in storageCell);
    }

    public void Set(in StorageCell storageCell, byte[] newValue)
    {
        if (_storageSlots.ContainsKey(storageCell.Address))
        {
            _storageSlots[storageCell.Address].Add(storageCell.Index);
        }
        else
        {
            _storageSlots.Add(storageCell.Address, new HashSet<UInt256> { storageCell.Index });
        }
        inner.Set(in storageCell, newValue);
    }

    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
    {
        if (_storageSlots.ContainsKey(storageCell.Address))
        {
            _storageSlots[storageCell.Address].Add(storageCell.Index);
        }
        else
        {
            _storageSlots.Add(storageCell.Address, new HashSet<UInt256> { storageCell.Index });
        }
        return inner.GetTransientState(in storageCell);
    }

    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
    {
        if (_storageSlots.ContainsKey(storageCell.Address))
        {
            _storageSlots[storageCell.Address].Add(storageCell.Index);
        }
        else
        {
            _storageSlots.Add(storageCell.Address, new HashSet<UInt256> { storageCell.Index });
        }
        inner.SetTransientState(in storageCell, newValue);
    }

    public void Reset(bool resetBlockChanges = true)
    {
        inner.Reset(resetBlockChanges);
    }

    public Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        return inner.TakeSnapshot(newTransactionStart);
    }

    public void WarmUp(AccessList? accessList)
    {
        inner.WarmUp(accessList);
    }

    public void WarmUp(Address address)
    {
        inner.WarmUp(address);
    }

    public void ClearStorage(Address address)
    {
        _storageSlots.TryAdd(address, []);
        inner.ClearStorage(address);
    }

    public void RecalculateStateRoot()
    {
        inner.RecalculateStateRoot();
    }

    public void DeleteAccount(Address address)
    {
        _storageSlots.TryAdd(address, []);
        inner.DeleteAccount(address);
    }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        _storageSlots.TryAdd(address, []);
        inner.CreateAccount(address, in balance, in nonce);
    }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        _storageSlots.TryAdd(address, []);
        inner.CreateAccountIfNotExists(address, in balance, in nonce);
    }

    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec,
        bool isGenesis = false)
    {
        _storageSlots.TryAdd(address, []);
        return inner.InsertCode(address, in codeHash, code, spec, isGenesis);
    }

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        _storageSlots.TryAdd(address, []);
        inner.AddToBalance(address, in balanceChange, spec);
    }

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        _storageSlots.TryAdd(address, []);
        return inner.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec);
    }

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        _storageSlots.TryAdd(address, []);
        inner.SubtractFromBalance(address, in balanceChange, spec);
    }

    public void IncrementNonce(Address address, UInt256 delta)
    {
        _storageSlots.TryAdd(address, []);
        inner.IncrementNonce(address, delta);
    }

    public void DecrementNonce(Address address, UInt256 delta)
    {
        _storageSlots.TryAdd(address, []);
        inner.DecrementNonce(address, delta);
    }

    public void SetNonce(Address address, in UInt256 nonce)
    {
        _storageSlots.TryAdd(address, []);
        inner.SetNonce(address, nonce);
    }

    public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
    {
        inner.Commit(releaseSpec, isGenesis, commitRoots);
    }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer? tracer, bool isGenesis = false, bool commitRoots = true)
    {
        inner.Commit(releaseSpec, tracer, isGenesis, commitRoots);
    }

    public void CommitTree(long blockNumber)
    {
        inner.CommitTree(blockNumber);
    }

    public ArrayPoolList<AddressAsKey>? GetAccountChanges()
    {
        return ((IWorldState)inner).GetAccountChanges();
    }

    public void ResetTransient()
    {
        inner.ResetTransient();
    }

    public IDisposable BeginScope(BlockHeader? baseBlock)
        => inner.BeginScope(baseBlock);

    public void CreateEmptyAccountIfDeleted(Address address)
        => inner.CreateEmptyAccountIfDeleted(address);
}
