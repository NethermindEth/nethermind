// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Evm.JsonTypes;

public class PostState
{

    public Hash256? StateRoot { get; set; }
    public Hash256? TxRoot { get; set; }
    public Hash256? ReceiptRoot { get; set; }
    public Hash256? LogsHash { get; set; }
    public Bloom? Bloom { get; set; }
    public TxReceipt[]? Receipts { get; set; }
    public RejectedTx[]? Rejected { get; set; }
    public UInt256? Difficulty { get; set; }
    public UInt256? GasUsed { get; set; }
    public Hash256? BaseFee { get; set; }
}

public class RejectedTx
{
    public RejectedTx(int index, string reason)
    {
        Index = index;
        Reason = reason;
    }

    public int Index { get; set; }
    public string? Reason { get; set; }
}

