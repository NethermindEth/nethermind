// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.Parallel;

/// <summary>
/// Decorates <see cref="IWorldStateScopeProvider"/> to intercept all reads/writes and feed them
/// to a <see cref="StateDiffRecorder"/>.
/// <para>
/// When <paramref name="bufferOnly"/> is <c>true</c> (parallel workers), the write batch does NOT
/// delegate to the inner scope — no trie root hash computation. When <c>false</c> (main state),
/// writes delegate to the inner scope normally.
/// </para>
/// </summary>
public class StateDiffScopeProviderDecorator(
    IWorldStateScopeProvider inner,
    StateDiffRecorder recorder,
    bool bufferOnly = false) : IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock) => inner.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock) =>
        new DecoratedScope(inner.BeginScope(baseBlock), recorder, bufferOnly);

    private class DecoratedScope : IWorldStateScopeProvider.IScope
    {
        private readonly IWorldStateScopeProvider.IScope _inner;
        private readonly StateDiffRecorder _recorder;
        private readonly bool _bufferOnly;
        private DecoratedCodeDb? _codeDb;

        public DecoratedScope(IWorldStateScopeProvider.IScope inner, StateDiffRecorder recorder, bool bufferOnly)
        {
            _inner = inner;
            _recorder = recorder;
            _bufferOnly = bufferOnly;
        }

        public Hash256 RootHash => _inner.RootHash;

        public void UpdateRootHash() => _inner.UpdateRootHash();

        public Account? Get(Address address)
        {
            Account? account = _inner.Get(address);
            _recorder.RecordAccountRead(address, account);
            return account;
        }

        public void HintGet(Address address, Account? account) => _inner.HintGet(address, account);

        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb ??= new DecoratedCodeDb(_inner.CodeDb, _recorder);

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new DecoratedStorageTree(_inner.CreateStorageTree(address), _recorder, address);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            _bufferOnly
                ? new BufferOnlyWriteBatch(_recorder)
                : new DelegatingWriteBatch(_inner.StartWriteBatch(estimatedAccountNum), _recorder);

        public void Commit(long blockNumber) => _inner.Commit(blockNumber);

        public void Dispose() => _inner.Dispose();
    }

    private class DecoratedStorageTree(
        IWorldStateScopeProvider.IStorageTree inner,
        StateDiffRecorder recorder,
        Address address) : IWorldStateScopeProvider.IStorageTree
    {
        public Hash256 RootHash => inner.RootHash;

        public byte[] Get(in UInt256 index)
        {
            byte[] value = inner.Get(in index);
            recorder.RecordStorageRead(new StorageCell(address, index));
            return value;
        }

        public void HintGet(in UInt256 index, byte[]? value) => inner.HintGet(in index, value);

        public byte[] Get(in ValueHash256 hash) => inner.Get(in hash);
    }

    // ── Buffer-only write batches (parallel workers: no trie delegation) ──────────────

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
            {
                foreach (BufferOnlyStorageWriteBatch batch in _childBatches)
                    batch.FlushTo(recorder);
            }
        }
    }

    private class BufferOnlyStorageWriteBatch(Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private List<(UInt256 Index, byte[] Value)>? _buffered;

        public void Set(in UInt256 index, byte[] value) =>
            (_buffered ??= []).Add((index, value));

        public void Clear() { }

        public void FlushTo(StateDiffRecorder recorder)
        {
            if (_buffered is not null)
            {
                foreach ((UInt256 index, byte[] value) in _buffered)
                    recorder.RecordStorageWrite(address, in index, value);
            }
        }

        public void Dispose() { }
    }

    // ── Delegating write batches (main state: full trie delegation + recording) ──────

    private class DelegatingWriteBatch(
        IWorldStateScopeProvider.IWorldStateWriteBatch inner,
        StateDiffRecorder recorder) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private List<DelegatingStorageWriteBatch>? _childBatches;

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated> OnAccountUpdated
        {
            add => inner.OnAccountUpdated += value;
            remove => inner.OnAccountUpdated -= value;
        }

        public void Set(Address key, Account? account)
        {
            recorder.RecordAccountWrite(key, account);
            inner.Set(key, account);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries)
        {
            DelegatingStorageWriteBatch batch = new(inner.CreateStorageWriteBatch(key, estimatedEntries), key);
            (_childBatches ??= []).Add(batch);
            return batch;
        }

        public void Dispose()
        {
            // Flush child storage batches to recorder on the main thread
            if (_childBatches is not null)
            {
                foreach (DelegatingStorageWriteBatch batch in _childBatches)
                    batch.FlushTo(recorder);
            }

            inner.Dispose();
        }
    }

    /// <summary>
    /// Delegates to the inner storage write batch AND buffers writes locally.
    /// Multiple instances are processed in parallel by <c>UpdateRootHashesMultiThread</c>,
    /// so writes are flushed to the recorder by the parent on the main thread.
    /// </summary>
    private class DelegatingStorageWriteBatch(
        IWorldStateScopeProvider.IStorageWriteBatch inner,
        Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private List<(UInt256 Index, byte[] Value)>? _buffered;

        public void Set(in UInt256 index, byte[] value)
        {
            (_buffered ??= []).Add((index, value));
            inner.Set(in index, value);
        }

        public void Clear() => inner.Clear();

        public void FlushTo(StateDiffRecorder recorder)
        {
            if (_buffered is not null)
            {
                foreach ((UInt256 index, byte[] value) in _buffered)
                    recorder.RecordStorageWrite(address, in index, value);
            }
        }

        public void Dispose() => inner.Dispose();
    }

    // ── Code DB ──────────────────────────────────────────────────────────────────────

    private class DecoratedCodeDb(
        IWorldStateScopeProvider.ICodeDb inner,
        StateDiffRecorder recorder) : IWorldStateScopeProvider.ICodeDb
    {
        public byte[]? GetCode(in ValueHash256 codeHash) => inner.GetCode(in codeHash);

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() =>
            new DecoratedCodeSetter(inner.BeginCodeWrite(), recorder);
    }

    private class DecoratedCodeSetter(
        IWorldStateScopeProvider.ICodeSetter inner,
        StateDiffRecorder recorder) : IWorldStateScopeProvider.ICodeSetter
    {
        public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code)
        {
            recorder.RecordCodeWrite(Address.Zero, in codeHash, code.ToArray());
            inner.Set(in codeHash, code);
        }

        public void Dispose() => inner.Dispose();
    }
}
