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
/// Decorates <see cref="IWorldStateScopeProvider"/> to intercept reads/writes for state diff recording
/// (worker scopes) and to inject buffered diffs into write batches (main scope).
/// <para>
/// When <paramref name="bufferOnly"/> is <c>true</c> (parallel workers), write batches buffer locally
/// and do not delegate to the inner scope. Reads are recorded into <paramref name="recorder"/>.
/// </para>
/// <para>
/// When used as main scope decorator (via DI), captures <c>LastBaseBlock</c> and injects pending
/// diffs from <see cref="ParallelBlockExecutionContext"/> into write batches created by
/// <c>WorldState.Commit(commitRoots: true)</c>.
/// </para>
/// </summary>
public class StateDiffScopeProviderDecorator : IWorldStateScopeProvider
{
    private readonly IWorldStateScopeProvider _inner;
    private readonly StateDiffRecorder? _recorder;
    private readonly bool _bufferOnly;
    private readonly ParallelBlockExecutionContext? _context;

    /// <summary>Constructor for Autofac decorator resolution (main scope — no recording, injects diffs).</summary>
    public StateDiffScopeProviderDecorator(IWorldStateScopeProvider inner, ParallelBlockExecutionContext context)
    {
        _inner = inner;
        _recorder = null;
        _bufferOnly = false;
        _context = context;
    }

    /// <summary>Constructor for manual creation (parallel workers — records reads/writes, buffer-only).</summary>
    public StateDiffScopeProviderDecorator(IWorldStateScopeProvider inner, StateDiffRecorder recorder, bool bufferOnly)
    {
        _inner = inner;
        _recorder = recorder;
        _bufferOnly = bufferOnly;
        _context = null;
    }

    public bool HasRoot(BlockHeader? baseBlock) => _inner.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        if (_context is not null)
            _context.LastBaseBlock = baseBlock;

        IWorldStateScopeProvider.IScope innerScope = _inner.BeginScope(baseBlock);

        return _bufferOnly
            ? new RecordingScope(innerScope, _recorder!)
            : new MainScope(innerScope, _context!);
    }

    // ── Main scope (no recording, injects buffered diffs) ────────────────────────

    private class MainScope(
        IWorldStateScopeProvider.IScope inner,
        ParallelBlockExecutionContext context) : IWorldStateScopeProvider.IScope
    {
        public Hash256 RootHash => inner.RootHash;
        public void UpdateRootHash() => inner.UpdateRootHash();
        public Account? Get(Address address) => inner.Get(address);
        public void HintGet(Address address, Account? account) => inner.HintGet(address, account);
        public IWorldStateScopeProvider.ICodeDb CodeDb => inner.CodeDb;
        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => inner.CreateStorageTree(address);
        public void Commit(long blockNumber) => inner.Commit(blockNumber);
        public void Dispose() => inner.Dispose();

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
        {
            IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = inner.StartWriteBatch(estimatedAccountNum);

            // Inject pending diffs into the write batch immediately.
            // These go to the trie/flat DB. OnAccountUpdated won't fire for these
            // (subscription happens after StartWriteBatch returns), but the trie is updated.
            List<TransactionStateDiff>? diffs = context.TakePendingDiffs();
            if (diffs is not null)
            {
                foreach (TransactionStateDiff diff in diffs)
                {
                    foreach ((Address address, Account? account) in diff.AccountWrites)
                        writeBatch.Set(address, account);

                    Dictionary<Address, List<(UInt256 Index, byte[] Value)>>? storageByAddress = null;
                    foreach ((Address address, UInt256 index, byte[] value) in diff.StorageWrites)
                    {
                        storageByAddress ??= [];
                        if (!storageByAddress.TryGetValue(address, out List<(UInt256, byte[])>? list))
                        {
                            list = [];
                            storageByAddress[address] = list;
                        }
                        list.Add((index, value));
                    }

                    if (storageByAddress is not null)
                    {
                        foreach (KeyValuePair<Address, List<(UInt256 Index, byte[] Value)>> kv in storageByAddress)
                        {
                            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch =
                                writeBatch.CreateStorageWriteBatch(kv.Key, kv.Value.Count);
                            foreach ((UInt256 index, byte[] value) in kv.Value)
                                storageBatch.Set(index, value);
                        }
                    }

                    if (diff.CodeWrites.Count > 0)
                    {
                        using IWorldStateScopeProvider.ICodeSetter codeSetter = inner.CodeDb.BeginCodeWrite();
                        foreach ((ValueHash256 codeHash, byte[] code) in diff.CodeWrites)
                            codeSetter.Set(codeHash, code);
                    }
                }
            }

            return writeBatch;
        }
    }

    // ── Recording scope (parallel workers — records reads/writes, buffer-only) ───

    private class RecordingScope : IWorldStateScopeProvider.IScope
    {
        private readonly IWorldStateScopeProvider.IScope _inner;
        private readonly StateDiffRecorder _recorder;
        private RecordingCodeDb? _codeDb;

        public RecordingScope(IWorldStateScopeProvider.IScope inner, StateDiffRecorder recorder)
        {
            _inner = inner;
            _recorder = recorder;
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
        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb ??= new RecordingCodeDb(_inner.CodeDb, _recorder);

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new RecordingStorageTree(_inner.CreateStorageTree(address), _recorder, address);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new BufferOnlyWriteBatch(_recorder);

        public void Commit(long blockNumber) => _inner.Commit(blockNumber);
        public void Dispose() => _inner.Dispose();
    }

    private class RecordingStorageTree(
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
