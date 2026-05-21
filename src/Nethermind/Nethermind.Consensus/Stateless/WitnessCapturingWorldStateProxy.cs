// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
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

/// <summary>
/// Transparent <see cref="IWorldState"/> decorator that records touched addresses, storage slots,
/// and bytecodes during block execution to build a <see cref="Witness"/> without a second execution.
/// </summary>
public sealed class WitnessCapturingWorldStateProxy(IWorldState inner) : IWorldState
{
    private Dictionary<Address, HashSet<UInt256>>? _storageSlots;
    private Dictionary<ValueHash256, byte[]>? _bytecodes;

    // 1 = armed, 0 = unarmed. Interlocked to be safe across threads.
    private int _armed;

    /// <summary>Allocates fresh tracking collections before a block execution.</summary>
    /// <exception cref="InvalidOperationException">Thrown if already armed.</exception>
    internal void Arm()
    {
        if (Interlocked.Exchange(ref _armed, 1) == 1)
            throw new InvalidOperationException(
                $"{nameof(WitnessCapturingWorldStateProxy)} is already armed. Nested arming is not supported.");

        _storageSlots = new Dictionary<Address, HashSet<UInt256>>();
        _bytecodes = new Dictionary<ValueHash256, byte[]>();
    }

    /// <summary>
    /// Disarms the proxy; tracking collections remain alive for <see cref="BuildWitness"/> to consume.
    /// Must be called from a <c>finally</c> block even if <c>ProcessOne</c> throws.
    /// </summary>
    internal void Disarm() => Interlocked.Exchange(ref _armed, 0);

    /// <summary>
    /// Builds a <see cref="Witness"/> from data recorded during the most recent armed execution.
    /// Consumes and nulls the tracking collections. Must be called between <see cref="Disarm"/> and the next <see cref="Arm"/>.
    /// </summary>
    internal Witness? BuildWitness(
        BlockHeader parentHeader,
        IStateReader stateReader,
        WitnessGeneratingHeaderFinder perBlockHeaderFinder)
    {
        Dictionary<Address, HashSet<UInt256>>? slots = Interlocked.Exchange(ref _storageSlots, null);
        Dictionary<ValueHash256, byte[]>? bytecodes = Interlocked.Exchange(ref _bytecodes, null);

        if (slots is null || bytecodes is null)
            return null;

        // Build Merkle proof nodes for every touched address and storage slot.
        // Proof traversal reads the parent state root (pre-execution), as required by stateless verifiers.
        // AccountProofCollector also covers reverted write paths missed by raw node interception.
        using PooledSet<byte[]> stateNodes = new(Bytes.EqualityComparer);

        foreach ((Address account, HashSet<UInt256> accountSlots) in slots)
        {
            AccountProofCollector collector = new(account, accountSlots);
            stateReader.RunTreeVisitor(collector, parentHeader);
            (IReadOnlyList<byte[]> accountProof, IReadOnlyList<byte[]>[] storageProof) = collector.GetRawResult();
            stateNodes.AddRange(accountProof);
            foreach (IReadOnlyList<byte[]> storage in storageProof)
                stateNodes.AddRange(storage);
        }

        // Include the state root node when no accounts were touched so the witness is non-empty.
        if (stateNodes.Count == 0)
        {
            AccountProofCollector emptyCollector = new(Address.Zero, (byte[][])[]);
            stateReader.RunTreeVisitor(emptyCollector, parentHeader);
            (IReadOnlyList<byte[]> emptyProof, _) = emptyCollector.GetRawResult();
            stateNodes.AddRange(emptyProof);
        }

        ArrayPoolList<byte[]> codes = new(bytecodes.Count);
        foreach (byte[] code in bytecodes.Values)
            codes.Add(code);

        ArrayPoolList<byte[]> state = new(stateNodes.Count);
        foreach (byte[] node in stateNodes)
            state.Add(node);

        // Populate headers from every BLOCKHASH accessed during execution (execution-apis#773).
        IOwnedReadOnlyList<byte[]> rawHeaders = perBlockHeaderFinder.GetWitnessHeaders(parentHeader.Hash!);
        ArrayPoolList<byte[]> headers = new(rawHeaders.Count);
        foreach (byte[] h in rawHeaders)
            headers.Add(h);
        rawHeaders.Dispose();

        return new Witness
        {
            State = state,
            Codes = codes,
            Keys = ArrayPoolList<byte[]>.Empty(),
            Headers = headers,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private HashSet<UInt256> RecordEmptySlots(Address address)
    {
        if (_armed == 0) return _emptySlots;

        ref HashSet<UInt256>? slot =
            ref CollectionsMarshal.GetValueRefOrAddDefault(_storageSlots!, address, out _);
        slot ??= [];
        return slot;
    }

    // Shared sentinel for the unarmed hot path, never mutated.
    private static readonly HashSet<UInt256> _emptySlots = [];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordSlot(in StorageCell storageCell)
    {
        // Only mutate when armed; _emptySlots must never be written to.
        if (_armed == 0) return;
        RecordEmptySlots(storageCell.Address).Add(storageCell.Index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordBytecode(byte[]? code)
    {
        if (_armed == 0 || code is not { Length: > 0 }) return;
        Hash256 hash = Keccak.Compute(code);
        _bytecodes!.TryAdd(hash, code);
    }

    public bool HasStateForBlock(BlockHeader? baseBlock) => inner.HasStateForBlock(baseBlock);
    public void Restore(Snapshot snapshot) => inner.Restore(snapshot);
    public Hash256 StateRoot => inner.StateRoot;
    public bool IsInScope => inner.IsInScope;
    public IWorldStateScopeProvider ScopeProvider => inner.ScopeProvider;
    public IDisposable BeginScope(BlockHeader? baseBlock) => inner.BeginScope(baseBlock);

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        RecordEmptySlots(address);
        return inner.TryGetAccount(address, out account);
    }

    public byte[]? GetCode(Address address)
    {
        RecordEmptySlots(address);
        byte[]? code = inner.GetCode(address);
        RecordBytecode(code);
        return code;
    }

    public byte[]? GetCode(in ValueHash256 codeHash)
    {
        byte[]? code = inner.GetCode(in codeHash);
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

    public ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell)
    {
        RecordSlot(in storageCell);
        return inner.GetOriginal(in storageCell);
    }

    public ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        RecordSlot(in storageCell);
        return inner.Get(in storageCell);
    }

    public void Set(in StorageCell storageCell, byte[] newValue)
    {
        RecordSlot(in storageCell);
        inner.Set(in storageCell, newValue);
    }

    // Transient storage has no trie representation — no witness capture needed.
    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell) =>
        inner.GetTransientState(in storageCell);

    public void SetTransientState(in StorageCell storageCell, byte[] newValue) =>
        inner.SetTransientState(in storageCell, newValue);

    public void Reset(bool resetBlockChanges = true) => inner.Reset(resetBlockChanges);

    public Snapshot TakeSnapshot(bool newTransactionStart = false) =>
        inner.TakeSnapshot(newTransactionStart);

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
        inner.SetNonce(address, in nonce);
    }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true) =>
        inner.Commit(releaseSpec, tracer, isGenesis, commitRoots);

    public void CommitTree(long blockNumber) => inner.CommitTree(blockNumber);
    public ArrayPoolList<AddressAsKey>? GetAccountChanges() => inner.GetAccountChanges();
    public void ResetTransient() => inner.ResetTransient();

    public void CreateEmptyAccountIfDeleted(Address address)
    {
        RecordEmptySlots(address);
        inner.CreateEmptyAccountIfDeleted(address);
    }

    public void AddAccountRead(Address address)
    {
        RecordEmptySlots(address);
        inner.AddAccountRead(address);
    }

    public IDisposable? BeginSystemAccountReadSuppression() =>
        inner.BeginSystemAccountReadSuppression();
}
