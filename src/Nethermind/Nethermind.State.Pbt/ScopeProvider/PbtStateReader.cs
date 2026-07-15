// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Pbt.ScopeProvider;

public class PbtStateReader([KeyFilter(DbNames.Code)] IDb codeDb, IPbtDbManager manager) : IStateReader
{
    public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
    {
        using PbtSnapshotBundle? bundle = manager.TryGatherBundle(new StateId(baseBlock), isReadOnly: true);
        if (bundle?.GetAccount(address) is { } accountClass)
        {
            account = accountClass.ToStruct();
            return true;
        }

        account = default;
        return false;
    }

    public ReadOnlySpan<byte> GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index)
    {
        using PbtSnapshotBundle? bundle = manager.TryGatherBundle(new StateId(baseBlock), isReadOnly: true);
        if (bundle is null) return [];

        EvmWord value = bundle.GetSlot(address, index);
        return EvmWordSlot.IsZero(value) ? [] : EvmWordSlot.ToStrippedBytes(value);
    }

    public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? [] : codeDb[codeHash.Bytes];

    public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == ValueKeccak.OfAnEmptyString ? [] : codeDb[codeHash.Bytes];

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx> =>
        throw new NotSupportedException("Trie visiting is not supported by the pbt state backend");

    public bool HasStateForBlock(BlockHeader? baseBlock) => manager.HasStateForBlock(new StateId(baseBlock));
}
