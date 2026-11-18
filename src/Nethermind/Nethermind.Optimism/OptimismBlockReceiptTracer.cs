// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Optimism;

public class OptimismBlockReceiptTracer(IOptimismSpecHelper opSpecHelper, IWorldState worldState) : BlockReceiptsTracer
{
    private readonly IOptimismSpecHelper _opSpecHelper = opSpecHelper;
    private readonly IWorldState _worldState = worldState;

    private ulong? _depositNonce;

    public override ITxTracer StartNewTxTrace(Transaction? tx)
    {
        _depositNonce = tx is not null && tx.IsDeposit() ? _worldState.GetNonce(tx.SenderAddress!) : null;
        return base.StartNewTxTrace(tx);
    }

    private (ulong?, ulong?) GetDepositReceiptData(BlockHeader header)
    {
        ArgumentNullException.ThrowIfNull(CurrentTx);

        ulong? version = CurrentTx.IsDeposit() && _opSpecHelper.IsCanyon(header) ? 1UL : null;
        return (_depositNonce, version);
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
