// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Stateless;

/// <summary>The outcome of <see cref="StatelessBlockProcessingEnv.Process"/>: the executed block, or why it is invalid.</summary>
public readonly record struct StatelessBlockProcessingResult(BlockHeader? Parent, Block? ProcessedBlock, TxReceipt[] Receipts, string? Error)
{
    public bool IsValid => Error is null;

    public static StatelessBlockProcessingResult Invalid(string? error) => new(null, null, [], error ?? "Invalid block");
}
