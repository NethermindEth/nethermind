// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Eth;

namespace Nethermind.Taiko.Rpc;

public class PreBuiltTxList(TransactionForRpc[] transactions, ulong estimatedGasUsed, ulong bytesLength)
{
    public TransactionForRpc[] Transactions { get; set; } = transactions;
    public ulong EstimatedGasUsed { get; set; } = estimatedGasUsed;
    public ulong BytesLength { get; set; } = bytesLength;
}
