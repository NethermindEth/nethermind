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

public class FlatStateReader([KeyFilter(DbNames.Code)] IDb _codeDb): IStateReader
{
    public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
    {
        throw new NotImplementedException();
    }

    public ReadOnlySpan<byte> GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index)
    {
        throw new NotImplementedException();
    }

    public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? [] : _codeDb[codeHash.Bytes];

    public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == Keccak.OfAnEmptyString.ValueHash256 ? [] : _codeDb[codeHash.Bytes];

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
    {
        throw new NotImplementedException();
    }

    public bool HasStateForBlock(BlockHeader? baseBlock)
    {
        throw new NotImplementedException();
    }
}
