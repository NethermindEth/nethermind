// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Trie;
using Paprika.Chain;
using Keccak = Paprika.Crypto.Keccak;

namespace Nethermind.Paprika;

public class PaprikaStateReader(IReadOnlyWorldStateAccessor readOnlyWorldStateAccessor, IDb codeDb): IStateReader
{

    public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
    {
        global::Paprika.Account acc = readOnlyWorldStateAccessor.GetAccount(
            (baseBlock?.StateRoot ?? Nethermind.Core.Crypto.Keccak.EmptyTreeHash).ToPaprikaKeccak(),
            address.ToPaprikaKeccak());

        account = acc.ToNethAccountStruct();
        return !account.IsTotallyEmpty;
    }

    public ReadOnlySpan<byte> GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index)
    {
        Span<byte> buffer = stackalloc byte[32];
        Keccak root = (baseBlock?.StateRoot ?? Nethermind.Core.Crypto.Keccak.EmptyTreeHash).ToPaprikaKeccak();
        buffer = readOnlyWorldStateAccessor.GetStorage(root, address.ToPaprikaKeccak(), index.SlotToPaprikaKeccak(), buffer);
        return buffer.IsEmpty ? null : buffer.ToArray();
    }

    public byte[]? GetCode(Hash256 codeHash) => codeHash == Nethermind.Core.Crypto.Keccak.OfAnEmptyString ? [] : codeDb[codeHash.Bytes];
    public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == Nethermind.Core.Crypto.Keccak.OfAnEmptyString.ValueHash256 ? [] : codeDb[codeHash.Bytes];


    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
    {
        throw new NotImplementedException();
    }

    public bool HasStateForBlock(BlockHeader? baseBlock)
    {
        Keccak root = (baseBlock?.StateRoot ?? Nethermind.Core.Crypto.Keccak.EmptyTreeHash).ToPaprikaKeccak();
        return readOnlyWorldStateAccessor.HasState(root);
    }
}
