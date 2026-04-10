// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Subprotocols.Eth.V70;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

public static class Eth72MessageCode
{
    public const int Status = Eth70MessageCode.Status;
    public const int GetReceipts = Eth70MessageCode.GetReceipts;
    public const int Receipts = Eth70MessageCode.Receipts;
    public const int BlockRangeUpdate = Eth70MessageCode.BlockRangeUpdate;
    public const int NewPooledTransactionHashes = 0x08;
    public const int GetPooledTransactions = 0x09;
    public const int PooledTransactions = 0x0A;
    public const int GetCells = 0x12;
    public const int Cells = 0x13;
}
