// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.Trie;

namespace Nethermind.State.Pbt.ScopeProvider;

public class PbtStateReader([KeyFilter(DbNames.Code)] IDb codeDb, IPbtDbManager manager) : IStateReader
{
    public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
    {
        using PbtReadOnlySnapshotBundle? bundle = manager.TryGatherReadOnlyBundle(new StateId(baseBlock));
        if (bundle?.GetAccount(address) is { } accountClass)
        {
            account = accountClass.ToStruct();
            return true;
        }

        account = default;
        return false;
    }

    public void GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index, out UInt256 value)
    {
        using PbtReadOnlySnapshotBundle? bundle = manager.TryGatherReadOnlyBundle(new StateId(baseBlock));
        if (bundle is null)
        {
            value = default;
            return;
        }

        HashedKey<PbtStorageTreeKey> runKey = PbtStateKey.StorageRun(address, PbtKeyDerivation.AddressKeyHash(address), index, out int slotIndex);
        EvmWord word = bundle.GetSlot(runKey, slotIndex);
        value = EvmWordSlot.ToUInt256(in word);
    }

    public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? [] : codeDb[codeHash.Bytes];

    public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == ValueKeccak.OfAnEmptyString ? [] : codeDb[codeHash.Bytes];

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx> =>
        throw new NotSupportedException("Trie visiting is not supported by the pbt state backend");

    public bool HasStateForBlock(BlockHeader? baseBlock) => manager.HasStateForBlock(new StateId(baseBlock));
}
