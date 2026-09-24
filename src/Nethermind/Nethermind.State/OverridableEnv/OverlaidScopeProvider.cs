// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State.OverridableEnv;

/// <summary>Lets an armed <see cref="IStateReadOverlay"/> answer account and slot reads before the scope underneath;
/// with the slot empty every call passes straight through. The overlaid storage tree never reports an empty root for
/// an account the overlay holds slots of, since the storage provider skips the tree entirely on an empty root.</summary>
public sealed class OverlaidScopeProvider(IWorldStateScopeProvider inner, StateReadOverlaySlot slot) : IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock) => inner.HasRoot(baseBlock);

    public bool HasStateForTargetBlock(BlockHeader targetBlock) => inner.HasStateForTargetBlock(targetBlock);

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope) =>
        Wrap(inner.TryBeginScopeAtTarget(targetBlock, metrics, out IWorldStateScopeProvider.IScope? innerScope), innerScope, out scope);

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope) =>
        Wrap(inner.TryBeginScope(baseBlock, metrics, out IWorldStateScopeProvider.IScope? innerScope), innerScope, out scope);

    private bool Wrap(bool opened, IWorldStateScopeProvider.IScope? innerScope, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        scope = opened ? new Scope(innerScope!, slot) : null;
        return opened;
    }

    private sealed class Scope(IWorldStateScopeProvider.IScope inner, StateReadOverlaySlot slot) : IWorldStateScopeProvider.IScope
    {
        public Hash256 RootHash => inner.RootHash;
        public bool StorageRootsAreAuthoritative => inner.StorageRootsAreAuthoritative;

        public IWorldStateScopeProvider.ICodeDb CodeDb => inner.CodeDb;

        public void UpdateRootHash() => inner.UpdateRootHash();

        public void HintWarmAccount(in ValueAddress address) => inner.HintWarmAccount(in address);

        public void HintWarmSlot(in ValueAddress address, in UInt256 index) => inner.HintWarmSlot(in address, in index);

        public Account? Get(Address address)
        {
            IStateReadOverlay? overlay = slot.Current;
            if (overlay is not null && overlay.TryGetAccountWithoutBasis(address, out Account? standalone)) return standalone;

            Account? underlying;
            if (slot.Cache is { } cache)
            {
                if (!cache.TryGetAccount(address, out underlying))
                {
                    underlying = inner.Get(address);
                    cache.SetAccount(address, underlying);
                }
            }
            else
            {
                underlying = inner.Get(address);
            }

            return overlay is not null && overlay.TryGetAccount(address, underlying, out Account? overlaid) ? overlaid : underlying;
        }

        public void HintGet(Address address, Account? account) => inner.HintGet(address, account);

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => new StorageTree(inner.CreateStorageTree(address), address, slot);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => inner.StartWriteBatch(estimatedAccountNum);

        public void Commit(ulong blockNumber) => inner.Commit(blockNumber);

        public void WriteBackCommittedState(Func<IWorldStateScopeProvider.IBlockChangeSnapshot> takeSnapshot) => inner.WriteBackCommittedState(takeSnapshot);

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => inner.HintBal(bal, sink);

        public void Dispose() => inner.Dispose();
    }

    private sealed class StorageTree(IWorldStateScopeProvider.IStorageTree inner, Address address, StateReadOverlaySlot slot) : IWorldStateScopeProvider.IStorageTree
    {
        public Hash256 RootHash
        {
            get
            {
                Hash256 root = inner.RootHash;
                return slot.Current is { } overlay && overlay.HasStorage(address) && root == Keccak.EmptyTreeHash
                    ? IStateReadOverlay.NonEmptyStorageRoot
                    : root;
            }
        }

        public void Get(in UInt256 index, out UInt256 value)
        {
            if (slot.Current is { } overlay && overlay.TryGetStorage(address, in index, out value)) return;

            if (slot.Cache is not { } cache)
            {
                inner.Get(in index, out value);
                return;
            }

            if (cache.TryGetSlot(address, in index, out value)) return;

            inner.Get(in index, out value);
            cache.SetSlot(address, in index, in value);
        }

        public void HintSet(in UInt256 index) => inner.HintSet(in index);
    }
}
