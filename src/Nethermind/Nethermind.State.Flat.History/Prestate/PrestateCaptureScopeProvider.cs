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
/// <remarks>
/// The capture runs sequentially (<c>ForceSequentialBlockAccessList</c>), where no access-list hint or apply is made.
/// If one is, its values did not come through <see cref="IWorldStateScopeProvider.IScope.Get"/>, so the capture of
/// that block fails rather than keep a value the soundness rule cannot prove. The storage-root contract is passed
/// through unchanged, so a capture never changes what the execution does: the builder runs on a trie-less history
/// scope, whose roots are already not authoritative, which is the read contract of a historical trace. One provider
/// serves one executor, which opens one scope at a time, so the wrappers are reused from block to block.
/// </remarks>
internal sealed class PrestateCaptureScopeProvider : IWorldStateScopeProvider
{
    private readonly IWorldStateScopeProvider _inner;
    private readonly PrestateCapture _capture;
    private readonly CaptureScope _scope;
    private readonly CaptureWriteBatch _writeBatch;
    private readonly Dictionary<AddressAsKey, CaptureStorageTree> _trees = [];
    private readonly Stack<CaptureStorageTree> _freeTrees = new();

    public PrestateCaptureScopeProvider(IWorldStateScopeProvider inner, PrestateCapture capture)
    {
        _inner = inner;
        _capture = capture;
        _scope = new CaptureScope(this);
        _writeBatch = new CaptureWriteBatch(capture);
    }

    public bool HasRoot(BlockHeader? baseBlock) => _inner.HasRoot(baseBlock);

    public bool HasStateForTargetBlock(BlockHeader targetBlock) => _inner.HasStateForTargetBlock(targetBlock);

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (!_inner.TryBeginScope(baseBlock, metrics, out IWorldStateScopeProvider.IScope? innerScope))
        {
            scope = null;
            return false;
        }

        if (baseBlock is null)
        {
            _capture.Fail("the scope has no parent header", unexpected: true);
            scope = innerScope;
            return true;
        }

        scope = _capture.OnScopeOpened(new StateId(baseBlock)) ? _scope.Bind(innerScope) : innerScope;
        return true;
    }

    /// <remarks>A record is keyed by the parent header's number and state root, which a target-anchored scope does not
    /// name, so that path is never recorded.</remarks>
    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        _capture.Fail("the scope was opened at a target block, without the parent header", unexpected: true);
        return _inner.TryBeginScopeAtTarget(targetBlock, metrics, out scope);
    }

    private IWorldStateScopeProvider.IStorageTree StorageTree(Address address, IWorldStateScopeProvider.IStorageTree innerTree)
    {
        if (_trees.TryGetValue(address, out CaptureStorageTree? tree) && ReferenceEquals(tree.Inner, innerTree)) return tree;

        if (tree is null)
        {
            tree = _freeTrees.TryPop(out CaptureStorageTree? free) ? free : new CaptureStorageTree(_capture);
            _trees[address] = tree;
        }

        return tree.Bind(innerTree, address);
    }

    private void ReleaseTrees()
    {
        foreach (CaptureStorageTree tree in _trees.Values) _freeTrees.Push(tree.Unbind());
        _trees.Clear();
    }

    private sealed class CaptureScope(PrestateCaptureScopeProvider provider) : IWorldStateScopeProvider.IScope
    {
        private IWorldStateScopeProvider.IScope _inner = null!;

        public CaptureScope Bind(IWorldStateScopeProvider.IScope inner)
        {
            _inner = inner;
            return this;
        }

        public Hash256 RootHash => _inner.RootHash;

        public bool StorageRootsAreAuthoritative => _inner.StorageRootsAreAuthoritative;

        public void UpdateRootHash() => _inner.UpdateRootHash();

        public void HintWarmAccount(Address address) => _inner.HintWarmAccount(address);

        public void HintWarmSlot(Address address, in UInt256 index) => _inner.HintWarmSlot(address, in index);

        public void Prefetch(Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken) => _inner.Prefetch(accounts, slots, cancellationToken);

        public bool SupportsPrefetch => _inner.SupportsPrefetch;

        public Account? Get(Address address)
        {
            Account? account = _inner.Get(address);
            provider._capture.OnAccountRead(address, account);
            return account;
        }

        public void HintGet(Address address, Account? account)
        {
            provider._capture.Fail(nameof(HintGet), unexpected: true);
            _inner.HintGet(address, account);
        }

        public IWorldStateScopeProvider.ICodeDb CodeDb => _inner.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => provider.StorageTree(address, _inner.CreateStorageTree(address));

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            provider._writeBatch.Bind(_inner.StartWriteBatch(estimatedAccountNum));

        public void Commit(ulong blockNumber)
        {
            provider._capture.OnCommit();
            provider.ReleaseTrees();
            _inner.Commit(blockNumber);
        }

        public void WriteBackCommittedState(Func<IWorldStateScopeProvider.IBlockChangeSnapshot> takeSnapshot) => _inner.WriteBackCommittedState(takeSnapshot);

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
        {
            provider._capture.Fail(nameof(HintBal), unexpected: true);
            return _inner.HintBal(bal, sink);
        }

        public void ApplyBal(ReadOnlyBlockAccessList bal, CancellationToken cancellationToken = default)
        {
            provider._capture.Fail(nameof(ApplyBal), unexpected: true);
            _inner.ApplyBal(bal, cancellationToken);
        }

        public void Dispose()
        {
            provider._capture.OnScopeClosed();
            provider.ReleaseTrees();
            IWorldStateScopeProvider.IScope inner = _inner;
            _inner = null!;
            inner.Dispose();
        }
    }

    private sealed class CaptureStorageTree(PrestateCapture capture) : IWorldStateScopeProvider.IStorageTree
    {
        private IWorldStateScopeProvider.IStorageTree _inner = null!;
        private Address _address = null!;

        public IWorldStateScopeProvider.IStorageTree Inner => _inner;

        public CaptureStorageTree Bind(IWorldStateScopeProvider.IStorageTree inner, Address address)
        {
            _inner = inner;
            _address = address;
            return this;
        }

        public CaptureStorageTree Unbind()
        {
            _inner = null!;
            _address = null!;
            return this;
        }

        public Hash256 RootHash => _inner.RootHash;

        public void Get(in UInt256 index, out UInt256 value)
        {
            _inner.Get(in index, out value);
            capture.OnSlotRead(_address, in index, in value);
        }

        public void HintSet(in UInt256 index) => _inner.HintSet(in index);

        public void HintSet(in UInt256 index, in UInt256 value) => _inner.HintSet(in index, in value);
    }

    /// <remarks>A storage write is recorded once per account when its batch is created, on the committing thread; the
    /// batch itself is returned undecorated, because its slot writes may run on parallel workers.</remarks>
    private sealed class CaptureWriteBatch(PrestateCapture capture) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private IWorldStateScopeProvider.IWorldStateWriteBatch _inner = null!;

        public CaptureWriteBatch Bind(IWorldStateScopeProvider.IWorldStateWriteBatch inner)
        {
            _inner = inner;
            return this;
        }

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated> OnAccountUpdated
        {
            add => _inner.OnAccountUpdated += value;
            remove => _inner.OnAccountUpdated -= value;
        }

        public bool AcceptsStorageWrites => _inner.AcceptsStorageWrites;

        public void Set(Address key, Account? account)
        {
            capture.OnAccountWritten(key, removedWithStorage: account is null);
            _inner.Set(key, account);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries)
        {
            capture.OnStorageWritten(key);
            return _inner.CreateStorageWriteBatch(key, estimatedEntries);
        }

        public void Dispose()
        {
            IWorldStateScopeProvider.IWorldStateWriteBatch inner = _inner;
            _inner = null!;
            inner.Dispose();
        }
    }
}
