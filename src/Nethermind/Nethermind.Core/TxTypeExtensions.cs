// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class TxTypeExtensions
{
    public static bool SupportsAccessList(this TxType txType)
        => txType >= TxType.AccessList && txType != TxType.DepositTx;

    public static bool Supports1559(this TxType txType)
        => txType >= TxType.EIP1559 && txType != TxType.DepositTx;

    public static bool SupportsBlobs(this TxType txType)
        => txType == TxType.Blob;

    public static bool SupportsAuthorizationList(this TxType txType)
        => txType == TxType.SetCode;
}
