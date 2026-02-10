// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc;

internal static partial class XdcExtensions
{
    public static bool IsSpecialTransaction(this Transaction currentTx, IXdcReleaseSpec spec)
        => currentTx.To is not null && ((currentTx.To == spec.BlockSignerContract) || (currentTx.To == spec.RandomizeSMCBinary));
    public static bool RequiresSpecialHandling(this Transaction currentTx, IXdcReleaseSpec spec)
            => IsSignTransaction(currentTx, spec)
            || IsLendingTransaction(currentTx, spec)
            || IsTradingTransaction(currentTx, spec)
            || IsLendingFinalizedTradeTransaction(currentTx, spec)
            || IsTradingStateTransaction(currentTx, spec);
    public static bool IsSignTransaction(this Transaction currentTx, IXdcReleaseSpec spec) => currentTx.To is not null && currentTx.To == spec.BlockSignerContract;
    public static bool IsTradingTransaction(this Transaction currentTx, IXdcReleaseSpec spec) => currentTx.To is not null && currentTx.To == spec.XDCXAddressBinary && spec.IsTIPXDCXMiner;
    public static bool IsLendingTransaction(this Transaction currentTx, IXdcReleaseSpec spec) => currentTx.To is not null && currentTx.To == spec.XDCXLendingAddressBinary && spec.IsTIPXDCXMiner;
    public static bool IsLendingFinalizedTradeTransaction(this Transaction currentTx, IXdcReleaseSpec spec) => currentTx.To is not null && currentTx.To == spec.XDCXLendingFinalizedTradeAddressBinary && spec.IsTIPXDCXMiner;
    public static bool IsTradingStateTransaction(this Transaction currentTx, IXdcReleaseSpec spec) => currentTx.To is not null && currentTx.To == spec.TradingStateAddressBinary && spec.IsTIPXDCXMiner;

    public static bool IsSkipNonceTransaction(this Transaction currentTx, IXdcReleaseSpec spec) =>
        currentTx.To is not null
            && (IsTradingStateTransaction(currentTx, spec)
            || IsTradingTransaction(currentTx, spec)
            || IsLendingTransaction(currentTx, spec)
            || IsLendingFinalizedTradeTransaction(currentTx, spec));

}
