// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Changesets;

public sealed class BulkFillScopeProvider(
    BulkFillSession session,
    ITrieNodeCache trieCache,
    IResourcePool resources,
    IFlatDbConfig config,
    ILogManager logs) : IWorldStateScopeProvider, IStateReader
{
    public bool HasRoot(BlockHeader? baseBlock) => session.IsReady && baseBlock is not null
        && baseBlock.Hash == session.BlockHash && new StateId(baseBlock) == session.CurrentState;

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
    {
        RequireRoot(baseBlock);
        ReadOnlySnapshotBundle readOnly = new(new SnapshotPooledList(0), session.CreateReader(), false, PersistedSnapshotStack.Empty(), isHistorical: true);
        SnapshotBundle bundle = new(readOnly, trieCache, resources, ResourcePool.Usage.ReadOnlyProcessingEnv);
        try
        {
            return new Scope(new FlatWorldStateScope(session.CurrentState, bundle, session,
                RejectCommit.Instance, config, new NoopTrieWarmer(), logs, isReadOnly: true), session);
        }
        catch
        {
            bundle.Dispose();
            throw;
        }
    }

    public bool HasStateForBlock(BlockHeader? baseBlock) => HasRoot(baseBlock);

    public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
    {
        RequireRoot(baseBlock);
        Account? value = session.CreateReader().GetAccount(address);
        account = value?.ToStruct() ?? default;
        return value is not null;
    }

    public void GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index, out UInt256 value)
    {
        RequireRoot(baseBlock);
        value = UInt256.Zero;
        session.CreateReader().TryGetSlot(address, index, ref value);
    }

    public byte[]? GetCode(Hash256 codeHash) => session.GetCode(codeHash.ValueHash256);
    public byte[]? GetCode(in ValueHash256 codeHash) => session.GetCode(codeHash);
    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null)
        where TCtx : struct, INodeContext<TCtx> => throw new NotSupportedException("Bulk replay has no trie node store.");

    private void RequireRoot(BlockHeader? header)
    {
        if (!HasRoot(header)) throw new InvalidOperationException("Bulk replay may only open its verified checkpoint.");
    }

    private sealed class Scope(FlatWorldStateScope inner, BulkFillSession session) : IWorldStateScopeProvider.IScope
    {
        public Hash256 RootHash => inner.RootHash;
        public bool StorageRootsAreAuthoritative => false;
        public IWorldStateScopeProvider.ICodeDb CodeDb => session;
        public Account? Get(Address address) => inner.Get(address);
        public void HintGet(Address address, Account? account) => inner.HintGet(address, account);

        public void HintSetAccount(Address address, Account? account) => inner.HintSetAccount(address, account);
        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => inner.CreateStorageTree(address);
        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => inner.StartWriteBatch(estimatedAccountNum);
        public void UpdateRootHash() => inner.UpdateRootHash();
        public void Commit(ulong blockNumber) => inner.Commit(blockNumber);
        public void WriteBackCommittedState(Func<IWorldStateScopeProvider.IBlockChangeSnapshot> takeSnapshot) => session.StageFinalState(takeSnapshot);
        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => Task.CompletedTask;
        public void Dispose() => inner.Dispose();
    }

    private sealed class RejectCommit : IFlatCommitTarget
    {
        public static readonly RejectCommit Instance = new();
        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource)
        {
            snapshot.Dispose();
            transientResource.ReleaseLease();
            throw new InvalidOperationException("Bulk replay must not publish live snapshots.");
        }
    }
}
