// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Snapshot;
using Nethermind.Trie;

namespace Nethermind.State.Pbt.ScopeProvider;

public class PbtStateReader([KeyFilter(DbNames.Code)] IDb codeDb, IPbtDbManager manager) : IStateReader
{
    public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
    {
        using PbtReadOnlySnapshotBundle bundle = GatherForRead(baseBlock);
        if (bundle.GetAccount(address)?.ToAccount() is { } accountClass)
        {
            account = accountClass.ToStruct();
            return true;
        }

        account = default;
        return false;
    }

    public void GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index, out UInt256 value)
    {
        using PbtReadOnlySnapshotBundle bundle = GatherForRead(baseBlock);
        value = bundle.GetSlot(address, index);
    }

    public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? [] : codeDb[codeHash.Bytes];

    public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == ValueKeccak.OfAnEmptyString ? [] : codeDb[codeHash.Bytes];

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx> =>
        throw new NotSupportedException("Trie visiting is not supported by the pbt state backend");

    public bool HasStateForBlock(BlockHeader? baseBlock) => manager.HasStateForBlock(new StateId(baseBlock));

    private PbtReadOnlySnapshotBundle GatherForRead(BlockHeader? baseBlock) => GatherForRead(baseBlock, stateId => manager.TryGatherReadOnlyBundle(stateId));

    /// <summary>Gathers the bundle a read at <paramref name="baseBlock"/> needs, reporting state that is not available as a missing trie node.</summary>
    public static TBundle GatherForRead<TBundle>(BlockHeader? baseBlock, Func<StateId, TBundle?> tryGather) where TBundle : class
    {
        try
        {
            StateId stateId = new(baseBlock);
            return tryGather(stateId)
                ?? throw new StateNotRetainedException($"No state available for block {stateId.BlockNumber} with state root {stateId.StateRoot}");
        }
        catch (StateUnavailableException e)
        {
            throw new MissingTrieNodeException($"State for block {baseBlock?.Number} is unavailable", null, TreePath.Empty, baseBlock?.StateRoot ?? Keccak.EmptyTreeHash, e);
        }
    }
}
