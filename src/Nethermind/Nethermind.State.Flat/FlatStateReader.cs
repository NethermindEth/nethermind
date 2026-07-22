// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public class FlatStateReader(
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IFlatDbManager flatDbManager,
    ILogManager logManager
) : IStateReader
{
    public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
    {
        using ReadOnlySnapshotBundle reader = GatherForRead(baseBlock);
        if (reader.GetAccount(address) is { } accountCls)
        {
            account = accountCls.ToStruct();
            return true;
        }

        account = default;
        return false;
    }

    public ReadOnlySpan<byte> GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index)
    {
        using ReadOnlySnapshotBundle reader = GatherForRead(baseBlock);
        return reader.GetSlot(address, index, reader.DetermineSelfDestructSnapshotIdx(address)) ?? [];
    }

    public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? [] : codeDb[codeHash.Bytes];

    public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == Keccak.OfAnEmptyString.ValueHash256 ? [] : codeDb[codeHash.Bytes];

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx>
    {
        StateId stateId = new(baseBlock);

        using ReadOnlySnapshotBundle reader = GatherForRead(baseBlock);

        // A historical bundle serves flat values only — there is no trie to walk at that block, so proof/visit
        // requests surface as state-unavailable instead of failing mid-walk with an internal error.
        if (reader.IsHistorical)
        {
            throw StateUnavailable(baseBlock, $"State proofs at historical block {stateId.BlockNumber} are not supported");
        }

        ReadOnlyStateTrieStoreAdapter trieStoreAdapter = new(reader);

        PatriciaTree patriciaTree = new(trieStoreAdapter, logManager);
        patriciaTree.Accept(treeVisitor, stateId.StateRoot.ToCommitment(), visitingOptions, diagnostics: diagnostics);
    }

    public bool HasStateForBlock(BlockHeader? baseBlock) => flatDbManager.HasStateForBlock(new StateId(baseBlock));

    /// <summary>
    /// Gathers a read bundle, translating "state unavailable" (pruned, orphaned, or gather timeout) into
    /// <see cref="MissingTrieNodeException"/> — the same contract the hash-based reader exposes — which JSON-RPC
    /// maps to a clean resource-not-found response instead of an internal error.
    /// </summary>
    private ReadOnlySnapshotBundle GatherForRead(BlockHeader? baseBlock)
    {
        try
        {
            return flatDbManager.GatherReadOnlySnapshotBundle(new StateId(baseBlock));
        }
        catch (InvalidOperationException e)
        {
            throw StateUnavailable(baseBlock, $"State for block {baseBlock?.Number} is unavailable", e);
        }
    }

    private static MissingTrieNodeException StateUnavailable(BlockHeader? baseBlock, string message, Exception? innerException = null) =>
        new(message, null, TreePath.Empty, baseBlock?.StateRoot ?? Keccak.EmptyTreeHash, innerException);
}
