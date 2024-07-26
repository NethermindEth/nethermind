// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Eth;

namespace Nethermind.Taiko.Rpc;

public class PreBuiltTxList(TransactionForRpc[] transactions, ulong estimatedGasUsed, long bytesLength)
{
    public TransactionForRpc[] Transactions { get; set; } = transactions;

    public string EstimatedGasUsed { get; set; } = estimatedGasUsed.ToString();

    public string BytesLength { get; set; } = bytesLength.ToString();
}
