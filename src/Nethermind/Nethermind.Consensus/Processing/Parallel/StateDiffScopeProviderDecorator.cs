// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.Parallel;

public class StateDiffScopeProviderDecorator : IWorldStateScopeProvider
{
    private readonly IWorldStateScopeProvider _inner;
    private readonly StateDiffRecorder? _recorder;
    private readonly bool _bufferOnly;
    private readonly ParallelBlockExecutionContext? _context;

    /// <summary>Main scope — reads from overlay, dumps on StartWriteBatch.</summary>
    public StateDiffScopeProviderDecorator(IWorldStateScopeProvider inner, ParallelBlockExecutionContext context)
    {
        _inner = inner;
        _recorder = null;
        _bufferOnly = false;
        _context = context;
    }

    /// <summary>Worker scope — records reads/writes, buffer-only write batches.</summary>
    public StateDiffScopeProviderDecorator(IWorldStateScopeProvider inner, StateDiffRecorder recorder, bool bufferOnly)
    {
        _inner = inner;
        _recorder = recorder;
        _bufferOnly = bufferOnly;
        _context = null;
    }

    // Overlay for re-execution — worker reads from this before falling through to inner
    private ParallelBlockExecutionContext? _overlay;
    public void SetOverlay(ParallelBlockExecutionContext overlay) => _overlay = overlay;
    public void ClearOverlay() => _overlay = null;

    public bool HasRoot(BlockHeader? baseBlock) => _inner.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        if (_context is not null)
            _context.LastBaseBlock = baseBlock;

        IWorldStateScopeProvider.IScope innerScope = _inner.BeginScope(baseBlock);

        return _bufferOnly
            ? new RecordingScope(innerScope, _recorder!, this)
            : new MainScope(innerScope, _context!);
    }

    // ── Main scope (overlay reads, dump on StartWriteBatch) ──────────────────────

    private class MainScope(
        IWorldStateScopeProvider.IScope inner,
        ParallelBlockExecutionContext context) : IWorldStateScopeProvider.IScope
    {
        public Hash256 RootHash => inner.RootHash;
        public void UpdateRootHash() => inner.UpdateRootHash();

        public Account? Get(Address address) =>
            context.AccountOverlay.TryGetValue(address, out Account? account)
                ? account
                : inner.Get(address);

        public void HintGet(Address address, Account? account) => inner.HintGet(address, account);

        public IWorldStateScopeProvider.ICodeDb CodeDb => new OverlayCodeDb(inner.CodeDb, context.CodeOverlay);

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new OverlayStorageTree(inner.CreateStorageTree(address), context.StorageOverlay, address);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
        {
            // Dump the overlay into the inner scope's write batch, then clear
            IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = inner.StartWriteBatch(estimatedAccountNum);
            context.DumpAndClear(writeBatch, inner);
            return writeBatch;
        }

        public void Commit(long blockNumber) => inner.Commit(blockNumber);
        public void Dispose() => inner.Dispose();
    }

    private class OverlayStorageTree(
        IWorldStateScopeProvider.IStorageTree inner,
        ConcurrentDictionary<StorageCell, byte[]> storageOverlay,
        Address address) : IWorldStateScopeProvider.IStorageTree
    {
        public Hash256 RootHash => inner.RootHash;

        public byte[] Get(in UInt256 index) =>
            storageOverlay.TryGetValue(new StorageCell(address, index), out byte[]? value)
                ? value
                : inner.Get(in index);

        public void HintGet(in UInt256 index, byte[]? value) => inner.HintGet(in index, value);
        public byte[] Get(in ValueHash256 hash) => inner.Get(in hash);
    }

    private class OverlayCodeDb(
        IWorldStateScopeProvider.ICodeDb inner,
        ConcurrentDictionary<ValueHash256, byte[]> codeOverlay) : IWorldStateScopeProvider.ICodeDb
    {
        public byte[]? GetCode(in ValueHash256 codeHash) =>
            codeOverlay.TryGetValue(codeHash, out byte[]? code)
                ? code
                : inner.GetCode(in codeHash);

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => inner.BeginCodeWrite();
    }

    // ── Recording scope (parallel workers) ───────────────────────────────────────

    private class RecordingScope : IWorldStateScopeProvider.IScope
    {
        private readonly IWorldStateScopeProvider.IScope _inner;
        private readonly StateDiffRecorder _recorder;
        private readonly StateDiffScopeProviderDecorator _parent;
        private RecordingCodeDb? _codeDb;

        public RecordingScope(IWorldStateScopeProvider.IScope inner, StateDiffRecorder recorder, StateDiffScopeProviderDecorator parent)
        {
            _inner = inner;
            _recorder = recorder;
            _parent = parent;
        }

        public Hash256 RootHash => _inner.RootHash;
        public void UpdateRootHash() => _inner.UpdateRootHash();

        public Account? Get(Address address)
        {
            // Check overlay first (set during re-execution)
            ParallelBlockExecutionContext? overlay = _parent._overlay;
            if (overlay is not null && overlay.AccountOverlay.TryGetValue(address, out Account? overlayAccount))
            {
                _recorder.RecordAccountRead(address, overlayAccount);
                return overlayAccount;
            }

            Account? account = _inner.Get(address);
            _recorder.RecordAccountRead(address, account);
            return account;
        }

        public void HintGet(Address address, Account? account) => _inner.HintGet(address, account);
        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb ??= new RecordingCodeDb(_inner.CodeDb, _recorder);

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new RecordingStorageTree(_inner.CreateStorageTree(address), _recorder, address, _parent);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new BufferOnlyWriteBatch(_recorder);

        public void Commit(long blockNumber) => _inner.Commit(blockNumber);
        public void Dispose() => _inner.Dispose();
    }

    private class RecordingStorageTree(
        IWorldStateScopeProvider.IStorageTree inner,
        StateDiffRecorder recorder,
        Address address,
        StateDiffScopeProviderDecorator parent) : IWorldStateScopeProvider.IStorageTree
    {
        public Hash256 RootHash => inner.RootHash;

        public byte[] Get(in UInt256 index)
        {
            ParallelBlockExecutionContext? overlay = parent._overlay;
            if (overlay is not null && overlay.StorageOverlay.TryGetValue(new StorageCell(address, index), out byte[]? overlayValue))
            {
                recorder.RecordStorageRead(new StorageCell(address, index));
                return overlayValue;
            }

            byte[] value = inner.Get(in index);
            recorder.RecordStorageRead(new StorageCell(address, index));
            return value;
        }

        public void HintGet(in UInt256 index, byte[]? value) => inner.HintGet(in index, value);
        public byte[] Get(in ValueHash256 hash) => inner.Get(in hash);
    }

    // ── Buffer-only write batches (parallel workers) ─────────────────────────────

    private class BufferOnlyWriteBatch(StateDiffRecorder recorder) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private List<BufferOnlyStorageWriteBatch>? _childBatches;
        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

        public void Set(Address key, Account? account)
        {
            recorder.RecordAccountWrite(key, account);
            OnAccountUpdated?.Invoke(this, new IWorldStateScopeProvider.AccountUpdated(key, account));
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries)
        {
            BufferOnlyStorageWriteBatch batch = new(key);
            (_childBatches ??= []).Add(batch);
            return batch;
        }

        public void Dispose()
        {
            if (_childBatches is not null)
                foreach (BufferOnlyStorageWriteBatch batch in _childBatches)
                    batch.FlushTo(recorder);
        }
    }

    private class BufferOnlyStorageWriteBatch(Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private List<(UInt256 Index, byte[] Value)>? _buffered;
        public void Set(in UInt256 index, byte[] value) => (_buffered ??= []).Add((index, value));
        public void Clear() { }
        public void FlushTo(StateDiffRecorder recorder)
        {
            if (_buffered is not null)
                foreach ((UInt256 index, byte[] value) in _buffered)
                    recorder.RecordStorageWrite(address, in index, value);
        }
        public void Dispose() { }
    }

    // ── Code DB (recording for workers) ──────────────────────────────────────────

    private class RecordingCodeDb(
        IWorldStateScopeProvider.ICodeDb inner,
        StateDiffRecorder recorder) : IWorldStateScopeProvider.ICodeDb
    {
        public byte[]? GetCode(in ValueHash256 codeHash) => inner.GetCode(in codeHash);
        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() =>
            new RecordingCodeSetter(inner.BeginCodeWrite(), recorder);
    }

    private class RecordingCodeSetter(
        IWorldStateScopeProvider.ICodeSetter inner,
        StateDiffRecorder recorder) : IWorldStateScopeProvider.ICodeSetter
    {
        public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code)
        {
            recorder.RecordCodeWrite(in codeHash, code.ToArray());
            inner.Set(in codeHash, code);
        }

        public void Dispose() => inner.Dispose();
    }
}
