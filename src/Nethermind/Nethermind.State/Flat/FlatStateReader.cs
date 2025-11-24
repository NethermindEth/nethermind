// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public class FlatStateReader(
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IFlatDiffRepository flatDiffRepository
): IStateReader
{
    public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
    {
        using SnapshotBundle reader = flatDiffRepository.GatherReaderAtBaseBlock(new StateId(baseBlock));
        if (reader is null)
        {
            account = default;
            return false;
        }

        if (reader.TryGetAccount(address, out Account? accountCls))
        {
            account = accountCls.ToStruct();
            return true;
        }

        account = default;
        return false;
    }

    // TODO: Why is it return span? How is it suppose to dispose itself?
    public ReadOnlySpan<byte> GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index)
    {
        using SnapshotBundle reader = flatDiffRepository.GatherReaderAtBaseBlock(new StateId(baseBlock));
        if (reader is null)
        {
            return Array.Empty<byte>();
        }

        if (reader.TryGetSlot(address, index, reader.DetermineSelfDestructStateIdx(address), out byte[] value))
        {
            return value;
        }

        return Array.Empty<byte>();
    }

    public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? [] : codeDb[codeHash.Bytes];

    public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == Keccak.OfAnEmptyString.ValueHash256 ? [] : codeDb[codeHash.Bytes];

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
    {
        throw new NotImplementedException();
    }

    public bool HasStateForBlock(BlockHeader? baseBlock)
    {
        return flatDiffRepository.HasStateForBlock(new StateId(baseBlock));
    }
}
