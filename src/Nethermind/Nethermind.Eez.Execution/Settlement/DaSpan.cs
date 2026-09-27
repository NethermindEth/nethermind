// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// The L2 blocks a batch settles, as its DA carries them: per block the beneficiary, the extra data and the
/// transactions that are not derived from L1. Transactions are kept in block-major order.
/// </summary>
public sealed record DaSpan(int[] TransactionCounts, Address[] Beneficiaries, ReadOnlyMemory<byte>[] ExtraData, ReadOnlyMemory<byte>[] Transactions)
{
    public int BlockCount => TransactionCounts.Length;
}

/// <summary>The DA stream of one batch: the rollup it belongs to, its block span and one action per effect entry.</summary>
public sealed record DaPayload(ulong RollupId, DaSpan Span, DaAction[] Actions);
