// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

/// <summary>
/// A <see cref="ParityLikeBlockTracer"/> variant that writes each completed tx-trace to a
/// bounded <see cref="Channel{T}"/> as soon as the EVM finishes the transaction, rather than
/// accumulating all traces in a list.
/// </summary>
/// <remarks>
/// Must be driven by a <c>Task.Run</c> background task (the producer). A separate async
/// consumer reads from the channel, serialises each trace, and flushes it to the network,
/// creating end-to-end backpressure: EVM execution stalls while the previous trace is in
/// flight, keeping peak memory at O(1) traces regardless of block size.
///
/// The one-trace "hold-back" (<see cref="_held"/>) is required because
/// <see cref="ReportReward"/> mutates the last trace after it has been produced —
/// we must not emit it until the next <see cref="AddTrace"/> call (or
/// <see cref="EndBlockTrace"/>) confirms that no further mutation will occur.
///
/// The blocking <c>WriteAsync(...).GetAwaiter().GetResult()</c> calls in
/// <see cref="AddTrace"/> and <see cref="EndBlockTrace"/> are intentional and safe:
/// they always execute on a thread-pool thread (via <c>Task.Run</c>), never on an
/// async-continuation thread, so there is no deadlock risk.
/// </remarks>
public sealed class ChannelParityLikeBlockTracer : ParityLikeBlockTracer
{
    private readonly ChannelWriter<ParityLikeTxTrace> _writer;
    private readonly CancellationToken _cancellationToken;

    // Held back until the next AddTrace or EndBlockTrace so ReportReward can still mutate it.
    private ParityLikeTxTrace? _held;

    public ChannelParityLikeBlockTracer(ParityTraceTypes types, ChannelWriter<ParityLikeTxTrace> writer, CancellationToken cancellationToken)
        : base(types)
    {
        _writer = writer;
        _cancellationToken = cancellationToken;
    }

    public ChannelParityLikeBlockTracer(Hash256 txHash, ParityTraceTypes types, ChannelWriter<ParityLikeTxTrace> writer, CancellationToken cancellationToken)
        : base(txHash, types)
    {
        _writer = writer;
        _cancellationToken = cancellationToken;
    }

    public ChannelParityLikeBlockTracer(IDictionary<Hash256, ParityTraceTypes> typesByTransaction, ChannelWriter<ParityLikeTxTrace> writer, CancellationToken cancellationToken)
        : base(typesByTransaction)
    {
        _writer = writer;
        _cancellationToken = cancellationToken;
    }

    protected override void AddTrace(ParityLikeTxTrace trace)
    {
        // _held is safe to emit now — it is no longer the last trace, so ReportReward
        // cannot modify it anymore. Block the producer thread until the consumer reads
        // (backpressure: EVM pauses while the trace is being serialised and flushed).
        if (_held is not null)
        {
            _writer.WriteAsync(_held, _cancellationToken).AsTask().GetAwaiter().GetResult();
        }
        _held = trace;

    }

    public override void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
        // ParityLikeBlockTracer.ReportReward normally mutates TxTraces.LastOrDefault().
        // Since we skip base.AddTrace, TxTraces is always empty here; apply the reward
        // directly to _held (the last trace produced) instead.
        if (_held is not null)
        {
            _held.Action = new ParityTraceAction
            {
                RewardType = rewardType,
                Value = rewardValue,
                Author = author,
                CallType = "reward",
                TraceAddress = [],
                Type = "reward",
                Result = null
            };
        }
    }

    public override void EndBlockTrace()
    {
        try
        {
            if (_held is not null)
            {
                _writer.WriteAsync(_held, _cancellationToken).AsTask().GetAwaiter().GetResult();
                _held = null;
            }
        }
        finally
        {
            // Always complete the channel so the consumer is not left waiting.
            _writer.TryComplete();
        }
    }
}
