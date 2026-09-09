// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Xdc;

public class XdcBlockhashStore(IBlockhashStore inner, IWorldState worldState) : IBlockhashStore, IHasAccessList
{
    private static readonly ValueHash256 CodeHash =
        ValueKeccak.Compute(Eip2935Constants.Code);

    public void ApplyBlockhashStateChanges(BlockHeader blockHeader, IReleaseSpec spec)
    {
        if (!spec.IsEip2935Enabled || blockHeader.IsGenesis || blockHeader.ParentHash is null) return;

        Address eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;

        ValueHash256 actualCodeHash = worldState.GetCodeHash(eip2935Account);
        bool hasCode = actualCodeHash != Keccak.OfAnEmptyString.ValueHash256;
        if (hasCode && actualCodeHash != CodeHash)
            throw new InvalidOperationException($"EIP-2935 contract code mismatch at {eip2935Account}.");

        if (!hasCode)
        {
            worldState.CreateAccountIfNotExists(eip2935Account, 0);
            worldState.SetNonce(eip2935Account, 1);
            worldState.InsertCode(eip2935Account, CodeHash, Eip2935Constants.Code, spec);
        }

        inner.ApplyBlockhashStateChanges(blockHeader, spec);
    }

    public Hash256? GetBlockHashFromState(BlockHeader currentBlockHeader, ulong requiredBlockNumber, IReleaseSpec spec) =>
        inner.GetBlockHashFromState(currentBlockHeader, requiredBlockNumber, spec);

    // The parent-hash write is delegated to the inner store, so its prewarm hint is exact here.
    public AccessList? GetAccessList(Block block, IReleaseSpec spec) =>
        (inner as IHasAccessList)?.GetAccessList(block, spec);
}
