// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Xdc;

public class XdcBlockhashStore(IWorldState worldState) : BlockhashStore(worldState)
{
    private readonly IWorldState _worldState = worldState;

    public override void ApplyBlockhashStateChanges(BlockHeader blockHeader, IReleaseSpec spec)
    {
        if (!spec.IsEip2935Enabled || blockHeader.IsGenesis || blockHeader.ParentHash is null) return;

        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;

        byte[] code = _worldState.GetCode(eip2935Account) ?? [];
        if (code.Length > 0 && !code.SequenceEqual(Eip2935Constants.Code))
            throw new InvalidOperationException($"EIP-2935 contract code mismatch at {eip2935Account}. Expected: {Eip2935Constants.Code.ToHexString()}, Actual: {code.ToHexString()}");

        if (code.Length == 0)
        {
            _worldState.CreateAccountIfNotExists(eip2935Account, 0);
            _worldState.SetNonce(eip2935Account, 1);
            _worldState.InsertCode(eip2935Account, ValueKeccak.Compute(Eip2935Constants.Code), Eip2935Constants.Code, spec);
        }

        base.ApplyBlockhashStateChanges(blockHeader, spec);
    }
}
