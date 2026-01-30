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
        using ReadOnlySnapshotBundle? reader = flatDbManager.GatherReadOnlySnapshotBundle(new StateId(baseBlock));
        if (reader is null)
        {
            account = default;
            return false;
        }

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
        using ReadOnlySnapshotBundle? reader = flatDbManager.GatherReadOnlySnapshotBundle(new StateId(baseBlock));
        if (reader is null)
        {
            return Array.Empty<byte>();
        }

        return reader.GetSlot(address, index, reader.DetermineSelfDestructSnapshotIdx(address)) ?? [];
    }

    public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? [] : codeDb[codeHash.Bytes];

    public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == Keccak.OfAnEmptyString.ValueHash256 ? [] : codeDb[codeHash.Bytes];

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
    {
        StateId stateId = new(baseBlock);

        using ReadOnlySnapshotBundle? reader = flatDbManager.GatherReadOnlySnapshotBundle(stateId);
        if (reader is null)
        {
            throw new InvalidOperationException($"State at {baseBlock} not found");
        }

        ReadOnlyStateTrieStoreAdapter trieStoreAdapter = new(reader);

        PatriciaTree patriciaTree = new PatriciaTree(trieStoreAdapter, logManager);
        patriciaTree.Accept(treeVisitor, stateId.StateRoot.ToCommitment(), visitingOptions);
    }

    public bool HasStateForBlock(BlockHeader? baseBlock) => flatDbManager.HasStateForBlock(new StateId(baseBlock));
}
