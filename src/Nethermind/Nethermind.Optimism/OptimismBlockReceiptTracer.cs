// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Evm.State;

namespace Nethermind.Optimism;

public class OptimismBlockReceiptTracer(IOptimismSpecHelper opSpecHelper, IWorldState worldState) : BlockReceiptsTracer
{
    private readonly IOptimismSpecHelper _opSpecHelper = opSpecHelper;
    private readonly IWorldState _worldState = worldState;

    private (ulong?, ulong?) GetDepositReceiptData(BlockHeader header)
    {
        ArgumentNullException.ThrowIfNull(CurrentTx);

        ulong? depositNonce = null;
        ulong? version = null;

        if (CurrentTx.IsDeposit())
        {
            depositNonce = _worldState.GetNonce(CurrentTx.SenderAddress!);
            // We write nonce after tx processing, so need to subtract one
            if (depositNonce > 0)
            {
                depositNonce--;
            }
            if (_opSpecHelper.IsCanyon(header))
            {
                version = 1;
            }
        }

        return (depositNonce, version);
    }

    protected override TxReceipt CreateReceipt()
    {
        (ulong? depositNonce, ulong? version) = GetDepositReceiptData(Block.Header);
        return new OptimismTxReceipt
        {
            DepositNonce = depositNonce,
            DepositReceiptVersion = version
        };
    }
}
