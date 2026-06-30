// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Bounds speculative cache-warming of a single transaction so the prewarmer stops once warming is no
/// longer productive.
/// </summary>
/// <remarks>
/// The EVM polls <see cref="IsCancelled"/> roughly every 1024 opcodes (when the tracer is cancelable).
/// Warming is cancelled when <paramref name="windowPolls"/> consecutive polls elapse without the
/// transaction reading any previously-unseen storage cell — i.e. it has stopped pulling in cold state and
/// is only burning compute. Pre-warming such compute cannot pre-load anything useful and instead contends
/// with the main thread executing the same transaction (catastrophic for a heavy compute-bound transaction
/// at a low index). State-heavy transactions keep discovering new cells, so they warm to completion and are
/// unaffected.
/// </remarks>
internal sealed class WarmupBudgetTracer(int windowPolls) : TxTracer, ITxTracer
{
    private readonly HashSet<StorageCell> _seenCells = [];
    private int _polls;
    private int _lastProductivePoll;

    // Explicit interface implementation: TxTracer leaves these as interface defaults, so they must be
    // re-mapped here for the EVM (which calls through ITxTracer) to observe them.
    bool ITxTracer.IsCancelable => true;

    // Evaluated by the EVM ~once per 1024 opcodes; cancel after windowPolls polls without a new cold cell.
    bool ITxTracer.IsCancelled => (++_polls - _lastProductivePoll) > windowPolls;

    public override bool IsTracingStorage => true;

    public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        if (_seenCells.Add(new StorageCell(address, in storageIndex)))
        {
            _lastProductivePoll = _polls;
        }
    }
}
