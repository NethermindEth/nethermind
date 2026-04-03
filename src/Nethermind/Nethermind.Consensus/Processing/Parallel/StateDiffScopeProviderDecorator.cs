// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.Parallel;

/// <summary>
/// Decorates <see cref="IWorldStateScopeProvider"/> to intercept all reads/writes and feed them
/// to a <see cref="StateDiffRecorder"/>. Used for both parallel workers and the main scope provider.
/// </summary>
public class StateDiffScopeProviderDecorator(IWorldStateScopeProvider inner, StateDiffRecorder recorder) : IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock) => inner.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock) =>
        new DecoratedScope(inner.BeginScope(baseBlock), recorder);

    private class DecoratedScope : IWorldStateScopeProvider.IScope
    {
        private readonly IWorldStateScopeProvider.IScope _inner;
        private readonly StateDiffRecorder _recorder;
        private DecoratedCodeDb? _codeDb;

        public DecoratedScope(IWorldStateScopeProvider.IScope inner, StateDiffRecorder recorder)
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

        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb ??= new DecoratedCodeDb(_inner.CodeDb, _recorder);

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new DecoratedStorageTree(_inner.CreateStorageTree(address), _recorder, address);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new DecoratedWriteBatch(_inner.StartWriteBatch(estimatedAccountNum), _recorder);

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

    private class DecoratedWriteBatch(
        IWorldStateScopeProvider.IWorldStateWriteBatch inner,
        StateDiffRecorder recorder) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
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

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new DecoratedStorageWriteBatch(inner.CreateStorageWriteBatch(key, estimatedEntries), recorder, key);

        public void Dispose() => inner.Dispose();
    }

    private class DecoratedStorageWriteBatch(
        IWorldStateScopeProvider.IStorageWriteBatch inner,
        StateDiffRecorder recorder,
        Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, byte[] value)
        {
            recorder.RecordStorageWrite(address, in index, value);
            inner.Set(in index, value);
        }

        public void Clear() => inner.Clear();

        public void Dispose() => inner.Dispose();
    }

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
            // Address is not available at this level; tracked via codeHash → code mapping.
            // During diff application, the address is correlated from account writes whose code hash changed.
            recorder.RecordCodeWrite(Address.Zero, in codeHash, code.ToArray());
            inner.Set(in codeHash, code);
        }

        public void Dispose() => inner.Dispose();
    }
}
