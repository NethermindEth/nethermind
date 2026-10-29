// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Evm.T8n;

/// <summary>
/// Records every account and storage slot written to the state trie.
/// </summary>
/// <remarks>
/// The trie is keyed by hashes, so the post-state cannot be dumped without the preimages; geth's <c>t8n</c> keeps a preimage
/// store for this. Starting from an empty trie, every leaf is written through this decorator, so the recorded keys cover
/// the whole post-state.
/// </remarks>
public class StateWriteRecorder(IWorldStateScopeProvider baseProvider) : IWorldStateScopeProvider
{
    private readonly Dictionary<AddressAsKey, HashSet<UInt256>> _writes = [];

    public IReadOnlyDictionary<AddressAsKey, HashSet<UInt256>> Writes => _writes;

    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public bool HasStateForTargetBlock(BlockHeader targetBlock) => baseProvider.HasStateForTargetBlock(targetBlock);

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (!baseProvider.TryBeginScopeAtTarget(targetBlock, metrics, out IWorldStateScopeProvider.IScope? baseScope))
        {
            scope = null;
            return false;
        }

        scope = new RecordingScope(baseScope, this);
        return true;
    }

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (!baseProvider.TryBeginScope(baseBlock, metrics, out IWorldStateScopeProvider.IScope? baseScope))
        {
            scope = null;
            return false;
        }

        scope = new RecordingScope(baseScope, this);
        return true;
    }

    private HashSet<UInt256> Record(Address address)
    {
        if (!_writes.TryGetValue(address, out HashSet<UInt256>? slots))
        {
            _writes[address] = slots = [];
        }

        return slots;
    }

    private sealed class RecordingScope(IWorldStateScopeProvider.IScope baseScope, StateWriteRecorder recorder) : IWorldStateScopeProvider.IScope
    {
        public Hash256 RootHash => baseScope.RootHash;
        public bool StorageRootsAreAuthoritative => baseScope.StorageRootsAreAuthoritative;
        public bool SupportsPrefetch => baseScope.SupportsPrefetch;
        public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

        public void Dispose() => baseScope.Dispose();

        public void UpdateRootHash() => baseScope.UpdateRootHash();

        public void HintWarmAccount(Address address) => baseScope.HintWarmAccount(address);

        public void HintWarmSlot(Address address, in UInt256 index) => baseScope.HintWarmSlot(address, in index);

        public void Prefetch(Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken) => baseScope.Prefetch(accounts, slots, cancellationToken);

        public Account? Get(Address address) => baseScope.Get(address);

        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => baseScope.CreateStorageTree(address);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new RecordingWriteBatch(baseScope.StartWriteBatch(estimatedAccountNum), recorder);

        public void Commit(ulong blockNumber) => baseScope.Commit(blockNumber);

        public void WriteBackCommittedState(Func<IWorldStateScopeProvider.IBlockChangeSnapshot> takeSnapshot) => baseScope.WriteBackCommittedState(takeSnapshot);

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => baseScope.HintBal(bal, sink);

        public void ApplyBal(ReadOnlyBlockAccessList bal, CancellationToken cancellationToken) => baseScope.ApplyBal(bal, cancellationToken);
    }

    private sealed class RecordingWriteBatch(IWorldStateScopeProvider.IWorldStateWriteBatch baseBatch, StateWriteRecorder recorder) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        public event EventHandler<IWorldStateScopeProvider.AccountUpdated> OnAccountUpdated
        {
            add => baseBatch.OnAccountUpdated += value;
            remove => baseBatch.OnAccountUpdated -= value;
        }

        public bool AcceptsStorageWrites => baseBatch.AcceptsStorageWrites;

        public void Set(Address key, Account? account)
        {
            recorder.Record(key);
            baseBatch.Set(key, account);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new RecordingStorageWriteBatch(baseBatch.CreateStorageWriteBatch(key, estimatedEntries), recorder.Record(key));

        public void Dispose() => baseBatch.Dispose();
    }

    private sealed class RecordingStorageWriteBatch(IWorldStateScopeProvider.IStorageWriteBatch baseBatch, HashSet<UInt256> slots) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, in UInt256 value)
        {
            slots.Add(index);
            baseBatch.Set(in index, in value);
        }

        public void Clear() => baseBatch.Clear();

        public void Dispose() => baseBatch.Dispose();
    }
}
