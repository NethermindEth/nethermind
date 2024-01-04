// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Optimism;

public class OptimismBlockReceiptTracer : BlockReceiptsTracer
{
    private readonly IOPConfigHelper _opConfigHelper;
    private readonly IWorldState _worldState;

    public OptimismBlockReceiptTracer(IOPConfigHelper opConfigHelper, IWorldState worldState)
    {
        _opConfigHelper = opConfigHelper;
        _worldState = worldState;
    }

    private (ulong?, ulong?) GetDepositReceiptData(BlockHeader header)
    {
        ArgumentNullException.ThrowIfNull(CurrentTx);

        ulong? depositNonce = null;
        ulong? version = null;

        if (CurrentTx.IsDeposit())
        {
            depositNonce = _worldState.GetNonce(CurrentTx.SenderAddress!).ToUInt64(null);
            if (depositNonce > 0)
            {
                depositNonce--;
            }
            if (_opConfigHelper.IsCanyon(header))
            {
                version = 1;
            }
        }

        return (depositNonce, version);
    }

    protected override TxReceipt BuildReceipt(Address recipient, long spentGas, byte statusCode, LogEntry[] logEntries, Hash256? stateRoot)
    {
        (ulong? depositNonce, ulong? version) = GetDepositReceiptData(Block.Header);
        OptimismTxReceipt receipt = new(base.BuildReceipt(recipient, spentGas, statusCode, logEntries, stateRoot))
            { DepositNonce = depositNonce, DepositReceiptVersion = version };

        return receipt;
    }
}
