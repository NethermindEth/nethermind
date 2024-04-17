// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Withdrawals;

public class BlockProductionWithdrawalProcessor : IWithdrawalProcessor
{
    private readonly IWithdrawalProcessor _processor;

    public BlockProductionWithdrawalProcessor(IWithdrawalProcessor processor) =>
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));

    public void ProcessWithdrawals(Block block, IReleaseSpec spec)
    {
        _processor.ProcessWithdrawals(block, spec);

        if (spec.WithdrawalsEnabled)
        {
            block.Header.WithdrawalsRoot = block.Withdrawals is null || block.Withdrawals.Length == 0
                ? Keccak.EmptyTreeHash
                : new WithdrawalTrie(block.Withdrawals!).RootHash;
        }
    }
}
