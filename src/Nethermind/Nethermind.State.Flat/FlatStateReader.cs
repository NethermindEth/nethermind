// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat;

public class FlatStateReader(
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IFlatDbManager flatDbManager,
    IHistoricalTrieVisitor historicalTrieVisitor
) : IStateReader
{
    public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
    {
        using ReadOnlySnapshotBundle reader = GatherForRead(baseBlock, ReaderFlags.None);
        if (reader.GetAccount(address) is { } accountCls)
        {
            account = accountCls.ToStruct();
            return true;
        }

        account = default;
        return false;
    }

    public void GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index, out UInt256 value)
    {
        using ReadOnlySnapshotBundle reader = GatherForRead(baseBlock, ReaderFlags.None);
        reader.GetSlot(address, index, reader.DetermineSelfDestructSnapshotIdx(address), out UInt256? slot);
        value = slot.GetValueOrDefault();
    }

    public void GetStorage(BlockHeader? baseBlock, Address address, ReadOnlySpan<UInt256> indexes, Span<UInt256> values)
    {
        using ReadOnlySnapshotBundle reader = GatherForRead(baseBlock, ReaderFlags.None);
        int selfDestructSnapshotIdx = reader.DetermineSelfDestructSnapshotIdx(address);
        for (int i = 0; i < indexes.Length; i++)
        {
            reader.GetSlot(address, indexes[i], selfDestructSnapshotIdx, out UInt256? slot);
            values[i] = slot.GetValueOrDefault();
        }
    }

    public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? [] : codeDb.Get(codeHash.Bytes, ReadFlags.HintCacheMiss);

    public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == Keccak.OfAnEmptyString.ValueHash256 ? [] : codeDb.Get(codeHash.Bytes, ReadFlags.HintCacheMiss);

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx> =>
        RunTreeVisitor(treeVisitor, new TreeVisitorAdapter<TCtx>(treeVisitor), baseBlock, visitingOptions, diagnostics);

    /// <inheritdoc/>
    public void RunTreeVisitor<TCtx>(INodeRecordVisitor<TCtx> visitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions, VisitingStats? diagnostics) where TCtx : struct, INodeContext<TCtx> =>
        RunTreeVisitor(new NodeRecordVisitorAdapter<TCtx>(visitor), visitor, baseBlock, visitingOptions, diagnostics);

    /// <param name="treeVisitor">The visitor as a historical state runs it.</param>
    /// <param name="recordVisitor">The same visitor as the walk over node records runs it.</param>
    private void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, INodeRecordVisitor<TCtx> recordVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions, VisitingStats? diagnostics) where TCtx : struct, INodeContext<TCtx>
    {
        StateId stateId = new(baseBlock);

        if (flatDbManager.IsHistoricalState(stateId))
        {
            RunHistoricalTreeVisitor(treeVisitor, baseBlock, stateId, visitingOptions, diagnostics);
            return;
        }

        ReadOnlySnapshotBundle reader = GatherForRead(baseBlock, treeVisitor.IsFullDbScan ? ReaderFlags.FullScan : ReaderFlags.None);
        if (reader.IsHistorical)
        {
            // Persistence moved past the block between the routing check and the gather.
            reader.Dispose();
            RunHistoricalTreeVisitor(treeVisitor, baseBlock, stateId, visitingOptions, diagnostics);
            return;
        }

        using (reader)
        {
            NodeRecordBufferVisitor<TCtx> visitor = new(reader, reader.StateSnapshot, recordVisitor, visitingOptions ?? VisitingOptions.Default, diagnostics);
            visitor.Accept(stateId.StateRoot.ToCommitment(), null);
        }
    }

    private void RunHistoricalTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, in StateId stateId, VisitingOptions? visitingOptions, VisitingStats? diagnostics) where TCtx : struct, INodeContext<TCtx>
    {
        try
        {
            if (historicalTrieVisitor.TryRunTreeVisitor(treeVisitor, stateId, visitingOptions, diagnostics)) return;
        }
        catch (StateUnavailableException e)
        {
            throw StateUnavailable(baseBlock, $"State proof at historical block {stateId.BlockNumber} is unavailable", e);
        }

        throw StateUnavailable(baseBlock, $"State proofs at historical block {stateId.BlockNumber} are not supported");
    }

    public bool HasStateForBlock(BlockHeader? baseBlock) => flatDbManager.HasStateForBlock(new StateId(baseBlock));

    /// <summary>
    /// Translates "state unavailable" into <see cref="MissingTrieNodeException"/> — the hash-based reader's
    /// contract — keeping the cause as inner, so JSON-RPC answers resource-unavailable (-32002) for a
    /// <see cref="StateNotRetainedException"/> and resource-not-found otherwise.
    /// </summary>
    private ReadOnlySnapshotBundle GatherForRead(BlockHeader? baseBlock, ReaderFlags readerFlags)
    {
        try
        {
            return flatDbManager.GatherReadOnlySnapshotBundle(new StateId(baseBlock), readerFlags);
        }
        catch (StateUnavailableException e)
        {
            throw StateUnavailable(baseBlock, $"State for block {baseBlock?.Number} is unavailable", e);
        }
    }

    private static MissingTrieNodeException StateUnavailable(BlockHeader? baseBlock, string message, Exception? innerException = null) =>
        new(message, null, TreePath.Empty, baseBlock?.StateRoot ?? Keccak.EmptyTreeHash, innerException);
}
