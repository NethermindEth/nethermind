// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Mcp.Dto;

public sealed record BlockTransactionSummary(
    string Hash,
    string From,
    string? To,
    string Value,
    long? GasUsed);

public sealed record BlockSummaryDto(
    long Number,
    string Hash,
    string ParentHash,
    long Timestamp,
    long GasUsed,
    long GasLimit,
    string? BaseFeePerGas,
    int TransactionCount,
    string FeeRecipient,
    string StateRoot,
    long Size,
    BlockTransactionSummary[] Transactions);
