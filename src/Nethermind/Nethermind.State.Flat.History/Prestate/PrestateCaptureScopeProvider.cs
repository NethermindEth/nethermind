// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History.Prestate;

/// <summary>Reports the reads and writes of the scope an armed <see cref="PrestateCapture"/> records. A scope opened
/// while the capture is not armed is returned undecorated, so the provider costs nothing between captures.</summary>
/// <remarks>The capture runs sequentially (<c>ForceSequentialBlockAccessList</c>), where no access-list hint or apply
/// is made. If one is, its values did not come through <see cref="IWorldStateScopeProvider.IScope.Get"/>, so the
/// capture of that block fails rather than keep a value the soundness rule cannot prove. The storage-root contract
/// is passed through unchanged, so a capture never changes what the execution does: the builder runs on a trie-less
/// history scope, whose roots are already not authoritative, which is the read contract of a historical trace.</remarks>
internal sealed class PrestateCaptureScopeProvider(IWorldStateScopeProvider inner, PrestateCapture capture) : IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock) => inner.HasRoot(baseBlock);

    public bool HasStateForTargetBlock(BlockHeader targetBlock) => inner.HasStateForTargetBlock(targetBlock);

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (!inner.TryBeginScope(baseBlock, metrics, out IWorldStateScopeProvider.IScope? innerScope))
        {
            scope = null;
            return false;
        }

        scope = Wrap(innerScope, baseBlock is null ? null : new StateId(baseBlock));
        return true;
    }

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (!inner.TryBeginScopeAtTarget(targetBlock, metrics, out IWorldStateScopeProvider.IScope? innerScope))
        {
            scope = null;
            return false;
        }

        scope = Wrap(innerScope, targetBlock.Number == 0 ? null : new StateId(targetBlock.Number - 1, innerScope.RootHash));
        return true;
    }

    private IWorldStateScopeProvider.IScope Wrap(IWorldStateScopeProvider.IScope innerScope, StateId? parent)
    {
        if (parent is not { } state)
        {
            capture.Fail("the scope has no parent state");
            return innerScope;
        }

        return capture.OnScopeOpened(state) ? new CaptureScope(innerScope, capture) : innerScope;
    }

    private sealed class CaptureScope(IWorldStateScopeProvider.IScope inner, PrestateCapture capture) : IWorldStateScopeProvider.IScope
    {
        private readonly Dictionary<AddressAsKey, CaptureStorageTree> _trees = [];

        public Hash256 RootHash => inner.RootHash;

        public bool StorageRootsAreAuthoritative => inner.StorageRootsAreAuthoritative;

        public void UpdateRootHash() => inner.UpdateRootHash();

        public void HintWarmAccount(Address address) => inner.HintWarmAccount(address);

        public void HintWarmSlot(Address address, in UInt256 index) => inner.HintWarmSlot(address, in index);

        public void Prefetch(Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken) => inner.Prefetch(accounts, slots, cancellationToken);

        public bool SupportsPrefetch => inner.SupportsPrefetch;

        public Account? Get(Address address)
        {
            Account? account = inner.Get(address);
            capture.OnAccountRead(address, account);
            return account;
        }

        public void HintGet(Address address, Account? account)
        {
            capture.Fail(nameof(HintGet));
            inner.HintGet(address, account);
        }

        public IWorldStateScopeProvider.ICodeDb CodeDb => inner.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
        {
            IWorldStateScopeProvider.IStorageTree innerTree = inner.CreateStorageTree(address);
            if (_trees.TryGetValue(address, out CaptureStorageTree? tree) && ReferenceEquals(tree.Inner, innerTree)) return tree;

            tree = new CaptureStorageTree(innerTree, address, capture);
            _trees[address] = tree;
            return tree;
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new CaptureWriteBatch(inner.StartWriteBatch(estimatedAccountNum), capture);

        public void Commit(ulong blockNumber)
        {
            capture.OnCommit();
            _trees.Clear();
            inner.Commit(blockNumber);
        }

        public void WriteBackCommittedState(Func<IWorldStateScopeProvider.IBlockChangeSnapshot> takeSnapshot) => inner.WriteBackCommittedState(takeSnapshot);

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
        {
            capture.Fail(nameof(HintBal));
            return inner.HintBal(bal, sink);
        }

        public void ApplyBal(ReadOnlyBlockAccessList bal, CancellationToken cancellationToken = default)
        {
            capture.Fail(nameof(ApplyBal));
            inner.ApplyBal(bal, cancellationToken);
        }

        public void Dispose()
        {
            capture.OnScopeClosed();
            inner.Dispose();
        }
    }

    private sealed class CaptureStorageTree(IWorldStateScopeProvider.IStorageTree inner, Address address, PrestateCapture capture) : IWorldStateScopeProvider.IStorageTree
    {
        public IWorldStateScopeProvider.IStorageTree Inner => inner;

        public Hash256 RootHash => inner.RootHash;

        public void Get(in UInt256 index, out UInt256 value)
        {
            inner.Get(in index, out value);
            capture.OnSlotRead(address, in index, in value);
        }

        public void HintSet(in UInt256 index) => inner.HintSet(in index);

        public void HintSet(in UInt256 index, in UInt256 value) => inner.HintSet(in index, in value);
    }

    private sealed class CaptureWriteBatch(IWorldStateScopeProvider.IWorldStateWriteBatch inner, PrestateCapture capture) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        public event EventHandler<IWorldStateScopeProvider.AccountUpdated> OnAccountUpdated
        {
            add => inner.OnAccountUpdated += value;
            remove => inner.OnAccountUpdated -= value;
        }

        public bool AcceptsStorageWrites => inner.AcceptsStorageWrites;

        public void Set(Address key, Account? account)
        {
            capture.OnAccountWritten(key, removedWithStorage: account is null);
            inner.Set(key, account);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new CaptureStorageWriteBatch(inner.CreateStorageWriteBatch(key, estimatedEntries), key, capture);

        public void Dispose() => inner.Dispose();
    }

    private sealed class CaptureStorageWriteBatch(IWorldStateScopeProvider.IStorageWriteBatch inner, Address address, PrestateCapture capture)
        : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, in UInt256 value)
        {
            capture.OnSlotWritten(address, in index);
            inner.Set(in index, in value);
        }

        public void Clear()
        {
            capture.OnStorageCleared(address);
            inner.Clear();
        }

        public void Dispose() => inner.Dispose();
    }
}
