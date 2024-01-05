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
    private IOPConfigHelper _opConfigHelper;
    private IWorldState _worldState;

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
            if (_opConfigHelper.IsCanyon(header))
            {
                version = 1;
            }
        }

        return (depositNonce == 0 ? 0 : depositNonce - 1, version);
    }

    protected override TxReceipt BuildReceipt(Address recipient, long spentGas, byte statusCode, LogEntry[] logEntries, Hash256? stateRoot)
    {
        (ulong? depositNonce, ulong? version) = GetDepositReceiptData(Block.Header);
        TxReceipt receipt = base.BuildReceipt(recipient, spentGas, statusCode, logEntries, stateRoot);
        receipt.DepositNonce = depositNonce;
        receipt.DepositReceiptVersion = version;
        return receipt;
    }
}
