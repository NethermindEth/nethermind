// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Collections.Pooled;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Transparent <see cref="IWorldState"/> decorator that records touched addresses, storage slots,
/// and bytecodes during block execution to build a <see cref="Witness"/> without a second execution.
/// Also owns the cross-thread rendezvous between the JSON-RPC handler (which awaits a witness)
/// and the block-processing thread (which produces one when a block matching the requested hash
/// is processed).
/// </summary>
public class WitnessCapturingWorldStateProxy(
    IWorldState inner,
    IStateReader stateReader,
    IHeaderFinder headerFinder,
    ILogManager logManager) : IWorldState
{
    private readonly ILogger _logger = logManager.GetClassLogger<WitnessCapturingWorldStateProxy>();
    private readonly ConcurrentDictionary<Hash256AsKey, TaskCompletionSource<Witness?>> _pending = new();

    /// <summary>
    /// Handler-side: register a pending witness request for <paramref name="blockHash"/> and return
    /// a <see cref="Task{T}"/> that completes when the block is processed (or is cancelled).
    /// </summary>
    public virtual Task<Witness?> RequestWitness(Hash256 blockHash)
    {
        // RunContinuationsAsynchronously: completion fires from the block-processing thread; we must
        // not run the handler's continuation inline there.
        TaskCompletionSource<Witness?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Witness?> effective = _pending.AddOrUpdate(
            blockHash,
            tcs,
            (_, existing) =>
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(WitnessCapturingWorldStateProxy)}: duplicate RequestWitness for {blockHash}. Replacing previous entry.");
                existing.TrySetCanceled();
                return tcs;
            });
        return effective.Task;
    }

    /// <summary>Handler-side: cancel a pending request (e.g. on exception path before drain).</summary>
    public virtual void CancelWitnessRequest(Hash256 blockHash)
    {
        if (_pending.TryRemove(blockHash, out TaskCompletionSource<Witness?>? tcs))
        {
            tcs.TrySetCanceled();
            if (_logger.IsTrace) _logger.Trace($"{nameof(WitnessCapturingWorldStateProxy)}: capture cancelled for {blockHash}");
        }
    }

    /// <summary>
    /// Decorator-side: arms capture if a request is pending and processing isn't read-only.
    /// Returns a no-op session otherwise.
    /// </summary>
    public WitnessCaptureSession BeginCapture(Hash256? blockHash, Hash256? parentHash, long blockNumber, ProcessingOptions options) =>
        WitnessCaptureSession.TryArm(this, blockHash, parentHash, blockNumber, options);

    /// <summary>Decorator-side: true iff a witness has been requested for this hash.</summary>
    internal bool HasPendingRequest(Hash256 blockHash) => _pending.ContainsKey(blockHash);

    /// <summary>Decorator-side: invoked from <see cref="WitnessCaptureSession.Drain"/>.</summary>
    internal void DrainTo(Hash256 blockHash, Hash256 parentStateRoot, Hash256 parentHash, long parentBlockNumber)
    {
        if (!_pending.TryRemove(blockHash, out TaskCompletionSource<Witness?>? tcs))
            return;

        Witness? witness = null;
        try
        {
            witness = BuildWitness(parentStateRoot, parentHash, parentBlockNumber);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"{nameof(WitnessCapturingWorldStateProxy)}: witness build failed for block {blockHash}", ex);
        }
        finally
        {
            tcs.SetResult(witness);
        }
    }

    private Dictionary<Address, HashSet<UInt256>>? _storageSlots;
    private Dictionary<ValueHash256, byte[]>? _bytecodes;

    // 1 = armed, 0 = unarmed. Interlocked to be safe across threads.
    private volatile int _armed;

    /// <exception cref="InvalidOperationException">Thrown if already armed; state is left unchanged.</exception>
    internal void Arm()
    {
        if (Interlocked.CompareExchange(ref _armed, 1, 0) != 0)
            throw new InvalidOperationException(
                $"{nameof(WitnessCapturingWorldStateProxy)} is already armed. Nested arming is not supported.");

        _storageSlots = [];
        _bytecodes = [];
    }

    internal bool IsArmed => _armed != 0;

    internal void Disarm()
    {
        Interlocked.Exchange(ref _storageSlots, null);
        Interlocked.Exchange(ref _bytecodes, null);
        Interlocked.Exchange(ref _armed, 0);
    }

    /// <summary>The state root the inner world state is anchored at — captured by the session at arm time.</summary>
    internal Hash256 InnerStateRoot => inner.StateRoot;

    /// <summary>Consumes the tracking collections to produce a <see cref="Witness"/>; null if not armed.</summary>
    internal Witness? BuildWitness(Hash256 parentStateRoot, Hash256 parentHash, long parentBlockNumber)
    {
        Dictionary<Address, HashSet<UInt256>>? slots = Interlocked.Exchange(ref _storageSlots, null);
        Dictionary<ValueHash256, byte[]>? bytecodes = Interlocked.Exchange(ref _bytecodes, null);

        if (slots is null || bytecodes is null)
            return null;

        // Construct the minimal BlockHeader IStateReader needs: number + StateRoot (StateReader uses
        // StateRoot only; FlatStateReader uses StateId(number, StateRoot) for snapshot lookup).
        BlockHeader parentView = new(Keccak.Zero, Keccak.Zero, Address.Zero, 0, parentBlockNumber, 0, 0, [])
        {
            StateRoot = parentStateRoot,
        };

        // AccountProofCollector also covers reverted write paths missed by raw node interception.
        using PooledSet<byte[]> stateNodes = new(Bytes.EqualityComparer);
        WitnessProofCollector.CollectAccountProofs(slots, stateReader, parentView, stateNodes);

        // Stateless verifiers expect at least the state root node when no account was touched.
        if (stateNodes.Count == 0)
        {
            AccountProofCollector emptyCollector = new(Address.Zero, (byte[][])[]);
            stateReader.RunTreeVisitor(emptyCollector, parentView);
            (IReadOnlyList<byte[]> emptyProof, _) = emptyCollector.GetRawResult();
            stateNodes.AddRange(emptyProof);
        }

        ArrayPoolList<byte[]> codes = new(bytecodes.Count);
        foreach (byte[] code in bytecodes.Values)
            codes.Add(code);

        ArrayPoolList<byte[]> state = new(stateNodes.Count);
        foreach (byte[] node in stateNodes)
            state.Add(node);

        WitnessGeneratingHeaderFinder perBlockHeaderFinder = new(headerFinder);
        IOwnedReadOnlyList<byte[]> rawHeaders = perBlockHeaderFinder.GetWitnessHeaders(parentHash);
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

    // Snapshot the dictionary at entry so a concurrent Disarm-null doesn't NRE this recorder.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordAddress(Address address)
    {
        Dictionary<Address, HashSet<UInt256>>? slots = _storageSlots;
        if (slots is null) return;
        CollectionsMarshal.GetValueRefOrAddDefault(slots, address, out _) ??= [];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordSlot(in StorageCell storageCell)
    {
        Dictionary<Address, HashSet<UInt256>>? slots = _storageSlots;
        if (slots is null) return;
        ref HashSet<UInt256>? set =
            ref CollectionsMarshal.GetValueRefOrAddDefault(slots, storageCell.Address, out _);
        set ??= [];
        set.Add(storageCell.Index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordBytecode(in ValueHash256 codeHash, byte[]? code)
    {
        Dictionary<ValueHash256, byte[]>? bytecodes = _bytecodes;
        if (bytecodes is null || code is not { Length: > 0 }) return;
        bytecodes.TryAdd(codeHash, code);
    }

    public bool HasStateForBlock(BlockHeader? baseBlock) => inner.HasStateForBlock(baseBlock);
    public void Restore(Snapshot snapshot) => inner.Restore(snapshot);
    public Hash256 StateRoot => inner.StateRoot;
    public bool IsInScope => inner.IsInScope;
    public IWorldStateScopeProvider ScopeProvider => inner.ScopeProvider;
    public IDisposable BeginScope(BlockHeader? baseBlock) => inner.BeginScope(baseBlock);

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        RecordAddress(address);
        return inner.TryGetAccount(address, out account);
    }

    public UInt256 GetNonce(Address address)
    {
        RecordAddress(address);
        return inner.GetNonce(address);
    }

    public bool IsStorageEmpty(Address address)
    {
        RecordAddress(address);
        return inner.IsStorageEmpty(address);
    }

    public bool HasCode(Address address)
    {
        RecordAddress(address);
        return inner.HasCode(address);
    }

    public bool IsNonZeroAccount(Address address, out bool accountExists)
    {
        RecordAddress(address);
        return inner.IsNonZeroAccount(address, out accountExists);
    }

    public bool IsDelegatedCode(Address address)
    {
        RecordAddress(address);
        byte[]? code = inner.GetCode(address);
        RecordBytecodeWithHashCompute(code);
        return Eip7702Constants.IsDelegatedCode(code);
    }

    public bool IsDelegatedCode(in ValueHash256 codeHash)
    {
        byte[]? code = inner.GetCode(in codeHash);
        RecordBytecode(in codeHash, code);
        return Eip7702Constants.IsDelegatedCode(code);
    }

    public byte[]? GetCode(Address address)
    {
        RecordAddress(address);
        byte[]? code = inner.GetCode(address);
        RecordBytecodeWithHashCompute(code);
        return code;
    }

    public byte[]? GetCode(in ValueHash256 codeHash)
    {
        // Address recording for this lookup happens via the GetCodeHash(Address) call upstream
        // in CodeInfoRepository.InternalGetCodeInfo — this overload has no Address.
        byte[]? code = inner.GetCode(in codeHash);
        RecordBytecode(in codeHash, code);
        return code;
    }

    // Slow path: the GetCode(Address) caller doesn't surface the hash, so recompute it. Fires only
    // on the parallel-BAL re-lookup branch in CodeInfoRepository; the canonical path uses the
    // GetCode(in ValueHash256) overload above where the hash is already known.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordBytecodeWithHashCompute(byte[]? code)
    {
        Dictionary<ValueHash256, byte[]>? bytecodes = _bytecodes;
        if (bytecodes is null || code is not { Length: > 0 }) return;
        Hash256 hash = Keccak.Compute(code);
        bytecodes.TryAdd(hash, code);
    }

    public bool IsContract(Address address)
    {
        RecordAddress(address);
        return inner.IsContract(address);
    }

    public bool AccountExists(Address address)
    {
        RecordAddress(address);
        return inner.AccountExists(address);
    }

    public bool IsDeadAccount(Address address)
    {
        RecordAddress(address);
        return inner.IsDeadAccount(address);
    }

    public ref readonly UInt256 GetBalance(Address address)
    {
        RecordAddress(address);
        return ref inner.GetBalance(address);
    }

    public ref readonly ValueHash256 GetCodeHash(Address address)
    {
        RecordAddress(address);
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
        RecordAddress(address);
        inner.ClearStorage(address);
    }

    public void RecalculateStateRoot() => inner.RecalculateStateRoot();

    public void DeleteAccount(Address address)
    {
        RecordAddress(address);
        inner.DeleteAccount(address);
    }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        RecordAddress(address);
        inner.CreateAccount(address, in balance, in nonce);
    }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        RecordAddress(address);
        inner.CreateAccountIfNotExists(address, in balance, in nonce);
    }

    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        RecordAddress(address);
        return inner.InsertCode(address, in codeHash, code, spec, isGenesis);
    }

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        RecordAddress(address);
        inner.AddToBalance(address, in balanceChange, spec, out oldBalance);
    }

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        RecordAddress(address);
        return inner.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec, out oldBalance);
    }

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        RecordAddress(address);
        inner.SubtractFromBalance(address, in balanceChange, spec, out oldBalance);
    }

    public void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce)
    {
        RecordAddress(address);
        inner.IncrementNonce(address, delta, out oldNonce);
    }

    public void DecrementNonce(Address address, UInt256 delta)
    {
        RecordAddress(address);
        inner.DecrementNonce(address, delta);
    }

    public void SetNonce(Address address, in UInt256 nonce)
    {
        RecordAddress(address);
        inner.SetNonce(address, in nonce);
    }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true) =>
        inner.Commit(releaseSpec, tracer, isGenesis, commitRoots);

    public void CommitTree(long blockNumber) => inner.CommitTree(blockNumber);
    public ArrayPoolList<AddressAsKey>? GetAccountChanges() => inner.GetAccountChanges();
    public void ResetTransient() => inner.ResetTransient();

    public void CreateEmptyAccountIfDeleted(Address address)
    {
        RecordAddress(address);
        inner.CreateEmptyAccountIfDeleted(address);
    }

    public void AddAccountRead(Address address)
    {
        RecordAddress(address);
        inner.AddAccountRead(address);
    }

    public IDisposable? BeginSystemAccountReadSuppression() =>
        inner.BeginSystemAccountReadSuppression();
}
