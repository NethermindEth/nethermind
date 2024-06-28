// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Taiko.Rpc;

public class PreBuiltTxList(byte[] transactions, ulong estimatedGasUsed, long bytesLength)
{
    public byte[] Transactions { get; set; } = transactions;
    public ulong EstimatedGasUsed { get; set; } = estimatedGasUsed;
    public long BytesLength { get; set; } = bytesLength;
}
