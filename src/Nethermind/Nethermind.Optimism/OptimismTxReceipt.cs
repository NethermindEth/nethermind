// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Optimism;

public class OptimismTxReceipt : TxReceipt
{
    public OptimismTxReceipt()
    {

    }

    public OptimismTxReceipt(TxReceipt receipt) : base(receipt)
    {

    }
    public ulong? DepositNonce { get; set; }
    public ulong? DepositReceiptVersion { get; set; }
}
