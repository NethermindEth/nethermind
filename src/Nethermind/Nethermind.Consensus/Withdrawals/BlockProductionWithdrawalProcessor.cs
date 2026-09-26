// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Withdrawals;

public class BlockProductionWithdrawalProcessor(IWithdrawalProcessor processor) : IWithdrawalProcessor
{
    private readonly IWithdrawalProcessor _processor = processor ?? throw new ArgumentNullException(nameof(processor));

    public void ProcessWithdrawals(Block block, IReleaseSpec spec)
    {
        // Set before the wrapped processor runs so a processor that derives the root itself
        // (OP Isthmus uses the L2ToL1MessagePasser storage root) is not overwritten.
        if (spec.WithdrawalsEnabled)
        {
            block.Header.WithdrawalsRoot = block.Withdrawals is null
                ? Keccak.EmptyTreeHash
                : WithdrawalTrie.CalculateRoot(block.Withdrawals!);
        }

        _processor.ProcessWithdrawals(block, spec);
    }
}
