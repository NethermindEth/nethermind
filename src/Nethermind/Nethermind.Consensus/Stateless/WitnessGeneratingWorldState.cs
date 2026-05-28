// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
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
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// <see cref="IWorldState"/> decorator that records every account/slot/bytecode access during block
/// execution and projects the captured set into a <see cref="Witness"/>.
/// </summary>
/// <remarks>
/// Two recording modes:
/// <list type="bullet">
///   <item>
///   <b>With a <see cref="WitnessCapturingTrieStore"/></b> (legacy <c>debug_executionWitness</c> flow):
///   the trie store also records raw touched nodes during a re-execution; <see cref="GetWitness"/> unions
///   those with proofs collected over the recorded addresses/slots.
///   </item>
///   <item>
///   <b>Without a trie store</b> (new <c>engine_newPayloadWithWitness</c> flow): the proxy is attached
///   to the main pipeline for one <c>ProcessOne</c> call; <see cref="GetWitness"/> builds the witness
///   purely from <see cref="WitnessProofCollector"/> over the recorded keys, then falls back to fetching
///   the state root proof if nothing was touched.
///   </item>
/// </list>
/// </remarks>
public class WitnessGeneratingWorldState(
    IWorldState inner,
    IStateReader stateReader,
    WitnessGeneratingHeaderFinder headerFinder,
    WitnessCapturingTrieStore? trieStore = null) : IWorldState
{
    private readonly object _lock = new();

    private readonly Dictionary<Address, HashSet<UInt256>> _storageSlots = [];

    private readonly Dictionary<ValueHash256, byte[]> _bytecodes = [];

    private readonly HashSet<Address> _deployedAddresses = [];

    // Hashes of bytecodes deployed within this block. These must not appear in the witness
    // codes section: a stateless verifier only needs pre-existing code to validate the
    // pre-state; newly-deployed code is self-evident from the block transactions.
    private readonly HashSet<ValueHash256> _deployedCodeHashes = [];

    /// <summary>
    /// Projects the recorded addresses/slots/bytecodes (and trie-touched nodes, when a capturing trie store
    /// was supplied) into a <see cref="Witness"/> rooted at <paramref name="parentHeader"/>.
    /// </summary>
    /// <param name="parentHeader">
    /// Parent block header used to anchor proof collection. Must carry the correct <c>StateRoot</c> and
    /// <c>Number</c>; the parent's hash is taken from <paramref name="parentHash"/> when supplied so the
    /// in-flight path can pass a stub header whose RLP-derived <c>Hash</c> would otherwise be wrong.
    /// </param>
    /// <param name="parentHash">
    /// Overrides <c>parentHeader.Hash</c> for the headers-section lookup. Lets the new endpoint avoid a DB
    /// lookup by passing a minimal header plus the known parent hash.
    /// </param>
    public Witness GetWitness(BlockHeader parentHeader, Hash256? parentHash = null)
    {
        // Two complementary sources of state nodes:
        //   1) When a WitnessCapturingTrieStore is wired in (re-execution path), trie nodes touched
        //      during execution arrive here via TouchedNodesRlp. This catches paths that were
        //      written-then-reverted (cached writes never round-trip through the trie visitor below).
        //   2) WitnessProofCollector runs a tree visitor over the recorded (address, slots) set —
        //      necessary in both modes for client compatibility (e.g. geth stateless verifiers).
        // When no trie store is supplied (in-flight capture), only source (2) is used; the
        // empty-state-nodes fallback at the bottom guarantees the root proof is always present.
        if (trieStore is not null && !trieStore.TouchedNodesRlp.Any())
        {
            // No trie nodes touched: lazy TrieNode handling can leave the root unrecorded for unknown
            // types. Explicitly resolve the root so the witness is never missing it.
            ITrieNodeResolver stateResolver = trieStore.GetTrieStore(null);
            TreePath path = TreePath.Empty;
            TrieNode node = stateResolver.FindCachedOrUnknown(path, parentHeader.StateRoot!);
            node.ResolveNode(stateResolver, path);
        }

        using PooledSet<byte[]> stateNodes = trieStore is not null
            ? new PooledSet<byte[]>(trieStore.TouchedNodesRlp, Bytes.EqualityComparer)
            : new PooledSet<byte[]>(Bytes.EqualityComparer);
        WitnessProofCollector.CollectAccountProofs(_storageSlots, stateReader, parentHeader, stateNodes);

        // In-flight path with no recorded accesses: stateless verifiers still expect the state root
        // node, so synthesise an empty-path proof.
        if (stateNodes.Count == 0)
        {
            AccountProofCollector emptyCollector = new(Address.Zero, (byte[][])[]);
            stateReader.RunTreeVisitor(emptyCollector, parentHeader);
            (IReadOnlyList<byte[]> emptyProof, _) = emptyCollector.GetRawResult();
            foreach (byte[] node in emptyProof)
                stateNodes.Add(node);
        }

        byte[][] sortedCodes = new byte[_bytecodes.Count][];
        int codeIdx = 0;
        foreach (byte[] code in _bytecodes.Values)
            sortedCodes[codeIdx++] = code;
        Array.Sort(sortedCodes, Bytes.Comparer);

        ArrayPoolList<byte[]> codes = new(sortedCodes.Length);
        foreach (byte[] code in sortedCodes)
            codes.Add(code);

        byte[][] sortedStateNodes = stateNodes.ToArray();
        Array.Sort(sortedStateNodes, Bytes.Comparer);

        ArrayPoolList<byte[]> state = new(sortedStateNodes.Length);
        foreach (byte[] node in sortedStateNodes)
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
            Headers = headerFinder.GetWitnessHeaders(parentHash ?? parentHeader.Hash!)
        };
    }

    public bool HasStateForBlock(BlockHeader? baseBlock) => inner.HasStateForBlock(baseBlock);

    public void Restore(Snapshot snapshot) => inner.Restore(snapshot);

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        RecordEmptySlots(address);
        return inner.TryGetAccount(address, out account);
    }

    public UInt256 GetNonce(Address address)
    {
        RecordEmptySlots(address);
        return inner.GetNonce(address);
    }

    public bool IsStorageEmpty(Address address)
    {
        RecordEmptySlots(address);
        return inner.IsStorageEmpty(address);
    }

    public bool HasCode(Address address)
    {
        RecordEmptySlots(address);
        byte[]? code = inner.GetCode(address);
        RecordBytecode(code);
        return code is { Length: > 0 };
    }

    public bool IsNonZeroAccount(Address address, out bool accountExists)
    {
        RecordEmptySlots(address);
        return inner.IsNonZeroAccount(address, out accountExists);
    }

    public bool IsDelegatedCode(Address address)
    {
        RecordEmptySlots(address);
        byte[]? code = inner.GetCode(address);
        RecordBytecode(code);
        return Eip7702Constants.IsDelegatedCode(code);
    }

    public bool IsDelegatedCode(in ValueHash256 codeHash)
    {
        byte[]? code = inner.GetCode(in codeHash);
        RecordBytecode(codeHash, code);
        return Eip7702Constants.IsDelegatedCode(code);
    }

    public void AddAccountRead(Address address)
    {
        RecordEmptySlots(address);
        inner.AddAccountRead(address);
    }

    public IDisposable? BeginSystemAccountReadSuppression() =>
        inner.BeginSystemAccountReadSuppression();

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
        RecordBytecode(codeHash, code);
        return code;
    }

    public bool IsContract(Address address)
    {
        RecordEmptySlots(address);
        byte[]? code = inner.GetCode(address);
        RecordBytecode(code);
        return code is { Length: > 0 };
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

    public ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell)
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

    public void WarmUp(Address address)
    {
        // Record the address so its state proof is included in the witness even when
        // execution reverts before any read (e.g. EXTCODECOPY OOG at cold access).
        RecordEmptySlots(address);
        // Also record the code so a stateless verifier can validate the code hash of
        // any account that incurred a cold-access charge (EIP-7928 requirement).
        RecordBytecode(inner.GetCode(address));
        inner.WarmUp(address);
    }

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
        if (!isGenesis)
        {
            _deployedAddresses.Add(address);
            // Track the hash so RecordBytecode/RecordCodeBytes can exclude newly-deployed
            // code from the witness codes section (EIP-7928: only pre-state code is needed).
            if (code.Length > 0)
                _deployedCodeHashes.Add(codeHash);
        }
        return inner.InsertCode(address, in codeHash, code, spec, isGenesis);
    }

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        RecordEmptySlots(address);
        inner.AddToBalance(address, in balanceChange, spec);
    }

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        RecordEmptySlots(address);
        inner.AddToBalance(address, in balanceChange, spec, out oldBalance);
    }

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        RecordEmptySlots(address);
        return inner.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec, out oldBalance);
    }

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        RecordEmptySlots(address);
        inner.SubtractFromBalance(address, in balanceChange, spec, out oldBalance);
    }

    public void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce)
    {
        RecordEmptySlots(address);
        inner.IncrementNonce(address, delta, out oldNonce);
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

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true) =>
        inner.Commit(releaseSpec, tracer, isGenesis, commitRoots);

    public void CommitTree(long blockNumber) => inner.CommitTree(blockNumber);

    public ArrayPoolList<AddressAsKey>? GetAccountChanges() => inner.GetAccountChanges();

    public void ResetTransient() => inner.ResetTransient();

    public IDisposable BeginScope(BlockHeader? baseBlock) => inner.BeginScope(baseBlock);

    public void CreateEmptyAccountIfDeleted(Address address)
    {
        RecordEmptySlots(address);
        inner.CreateEmptyAccountIfDeleted(address);
    }

    private void RecordSlot(in StorageCell storageCell)
    {
        lock (_lock)
        {
            ref HashSet<UInt256>? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_storageSlots, storageCell.Address, out _);
            slot ??= [];
            slot.Add(storageCell.Index);
        }
    }

    private HashSet<UInt256> RecordEmptySlots(Address address)
    {
        lock (_lock)
        {
            ref HashSet<UInt256>? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_storageSlots, address, out _);
            slot ??= [];
            return slot;
        }
    }

    private void RecordBytecode(byte[]? code)
    {
        // Slow path: caller didn't surface the hash, so recompute it.
        if (code is { Length: > 0 })
        {
            // EIP-7702 delegation designators are 23-byte pointers, not executable bytecode.
            // Stateless verifiers do not need them in the witness codes section (EIP-7928).
            if (Eip7702Constants.IsDelegatedCode(code))
                return;

            Hash256 codeHash = Keccak.Compute(code);
            // Skip bytecodes deployed in this block — a stateless verifier only needs
            // pre-existing code to validate the pre-state (EIP-7928).
            lock (_lock)
            {
                if (!_deployedCodeHashes.Contains(codeHash))
                    _bytecodes.TryAdd(codeHash, code);
            }
        }
    }

    private void RecordBytecode(in ValueHash256 codeHash, byte[]? code)
    {
        // Fast path: hash already known.
        // EIP-7702 delegation designators are 23-byte pointers, not executable bytecode.
        // Stateless verifiers do not need them in the witness codes section (EIP-7928).
        if (code is { Length: > 0 } && !Eip7702Constants.IsDelegatedCode(code))
        {
            lock (_lock)
            {
                if (!_deployedCodeHashes.Contains(codeHash))
                    _bytecodes.TryAdd(codeHash, code);
            }
        }
    }

    internal void RecordBlockAccessList(ReadOnlyBlockAccessList bal)
    {
        lock (_lock)
        {
            foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
            {
                ref HashSet<UInt256>? slots = ref CollectionsMarshal.GetValueRefOrAddDefault(_storageSlots, accountChanges.Address, out _);
                slots ??= [];
                foreach (ReadOnlySlotChanges slotChanges in accountChanges.StorageChanges)
                {
                    slots.Add(slotChanges.Key);
                }
                foreach (UInt256 readSlot in accountChanges.StorageReads)
                {
                    slots.Add(readSlot);
                }
            }
        }
    }

    internal void RecordCodeBytes(ReadOnlyMemory<byte> code)
    {
        if (code.Length > 0)
        {
            byte[] codeBytes = code.ToArray();
            Hash256 codeHash = Keccak.Compute(codeBytes);
            // Skip bytecodes deployed in this block (EIP-7928: only pre-state code is needed).
            lock (_lock)
            {
                if (!_deployedCodeHashes.Contains(codeHash))
                    _bytecodes.TryAdd(codeHash, codeBytes);
            }
        }
    }

    internal void RecordSystemContractAccess(Address address, UInt256 slotIndex, byte[]? code)
    {
        lock (_lock)
        {
            ref HashSet<UInt256>? slots = ref CollectionsMarshal.GetValueRefOrAddDefault(_storageSlots, address, out _);
            slots ??= [];
            slots.Add(slotIndex);
            RecordBytecode(code);
        }
    }

    internal void RecordSystemContractAccountAccess(Address address, byte[]? code)
    {
        lock (_lock)
        {
            ref HashSet<UInt256>? slots = ref CollectionsMarshal.GetValueRefOrAddDefault(_storageSlots, address, out _);
            slots ??= [];
            RecordBytecode(code);
        }
    }

    public void RecordAccountAccess(Address address)
    {
        RecordEmptySlots(address);
        inner.RecordAccountAccess(address);
    }

    public void RecordBytecodeAccess(Address address)
    {
        RecordEmptySlots(address);
        try
        {
            RecordBytecode(inner.GetCode(address));
        }
        catch (InvalidOperationException)
        {
            // Code is missing from the database (e.g. selfdestructed or not committed yet).
        }
        inner.RecordBytecodeAccess(address);
    }
}
