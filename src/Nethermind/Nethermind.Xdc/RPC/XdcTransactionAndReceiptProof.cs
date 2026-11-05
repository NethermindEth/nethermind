// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Xdc.RPC;

public sealed class XdcTransactionAndReceiptProof
{
    public Hash256 BlockHash { get; init; }
    public Hash256 TxRoot { get; init; }
    public Hash256 ReceiptRoot { get; init; }
    public string Key { get; init; } = string.Empty;
    public string[] TxProofKeys { get; init; } = [];
    public string[] TxProofValues { get; init; } = [];
    public string[] ReceiptProofKeys { get; init; } = [];
    public string[] ReceiptProofValues { get; init; } = [];
}
