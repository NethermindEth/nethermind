// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Xdc;

public class XdcBlockhashStore(IWorldState worldState) : BlockhashStore(worldState)
{
    private static readonly ValueHash256 CodeHash = ValueKeccak.Compute(Eip2935Constants.Code);

    private readonly IWorldState _worldState = worldState;

    public override void ApplyBlockhashStateChanges(BlockHeader blockHeader, IReleaseSpec spec)
    {
        if (!spec.IsEip2935Enabled || blockHeader.IsGenesis || blockHeader.ParentHash is null) return;

        Address eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;

        ValueHash256 actualCodeHash = _worldState.GetCodeHash(eip2935Account);
        bool hasCode = actualCodeHash != Keccak.OfAnEmptyString.ValueHash256;
        if (hasCode && actualCodeHash != CodeHash)
            throw new InvalidOperationException($"EIP-2935 contract code mismatch at {eip2935Account}.");

        if (!hasCode)
        {
            _worldState.CreateAccountIfNotExists(eip2935Account, 0);
            _worldState.SetNonce(eip2935Account, 1);
            _worldState.InsertCode(eip2935Account, CodeHash, Eip2935Constants.Code, spec);
        }

        base.ApplyBlockhashStateChanges(blockHeader, spec);
    }
}
