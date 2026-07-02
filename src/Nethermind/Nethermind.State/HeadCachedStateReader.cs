// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State;

/// <summary>
/// Decorates an <see cref="IStateReader"/> with the cross-block <see cref="HeadStateCache"/> so direct
/// state RPCs (<c>eth_getBalance</c>, <c>eth_getStorageAt</c>, <c>eth_getTransactionCount</c>, …) that read
/// at the head (or a tracked ancestor) are served as O(1) lookups instead of trie traversals. Reads at any
/// other block, code reads, and tree visiting pass straight through.
/// </summary>
/// <remarks>
/// Must wrap the <b>raw</b> reader and must not be used by <see cref="HeadStateCacheUpdater"/>'s refresh
/// (which reads the new head to populate the cache) — that would be circular. The updater is wired with
/// <see cref="IWorldStateManager.GlobalStateReader"/> (undecorated) for exactly this reason.
/// </remarks>
public sealed class HeadCachedStateReader(IStateReader inner, HeadStateCache cache) : IStateReader
{
    public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
    {
        AddressAsKey key = address;
        if (baseBlock?.Hash is not null
            && cache.TrySnapshot(baseBlock.Hash, out HeadStateSnapshot snapshot)
            && snapshot.IsCurrent
            && !snapshot.ChangedInWindow(in key))
        {
            if (cache.Accounts.TryGetValue(in key, out Account? cached) && snapshot.IsCurrent)
            {
                account = cached?.ToStruct() ?? default;
                return cached is not null;
            }

            bool found = inner.TryGetAccount(baseBlock, address, out account);
            Account? toCache = found
                ? new Account(account.Nonce, account.Balance, account.StorageRoot.ToCommitment(), account.CodeHash.ToCommitment())
                : null;
            cache.TryBackfillAccount(in key, toCache, snapshot.Generation);
            return found;
        }

        return inner.TryGetAccount(baseBlock, address, out account);
    }

    public ReadOnlySpan<byte> GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index)
    {
        StorageCell cell = new(address, in index);
        if (baseBlock?.Hash is not null
            && cache.TrySnapshot(baseBlock.Hash, out HeadStateSnapshot snapshot)
            && snapshot.IsCurrent
            && !snapshot.ChangedInWindow(in cell))
        {
            if (cache.Storage.TryGetValue(in cell, out byte[]? cached) && snapshot.IsCurrent)
            {
                return cached;
            }

            byte[] loaded = inner.GetStorage(baseBlock, address, in index).ToArray();
            cache.TryBackfillStorage(in cell, loaded, snapshot.Generation);
            return loaded;
        }

        return inner.GetStorage(baseBlock, address, in index);
    }

    public byte[]? GetCode(Hash256 codeHash) => inner.GetCode(codeHash);
    public byte[]? GetCode(in ValueHash256 codeHash) => inner.GetCode(in codeHash);
    public bool HasStateForBlock(BlockHeader? baseBlock) => inner.HasStateForBlock(baseBlock);

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null)
        where TCtx : struct, INodeContext<TCtx>
        => inner.RunTreeVisitor(treeVisitor, baseBlock, visitingOptions, diagnostics);
}
