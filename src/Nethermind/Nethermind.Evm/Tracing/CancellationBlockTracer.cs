// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class CancellationBlockTracer(IBlockTracer innerTracer, CancellationToken token = default) : IBlockTracer
    {
        private bool _isTracingRewards;
        private CancellationTxTracer? _txTracer;

        public bool IsTracingRewards
        {
            get => _isTracingRewards || innerTracer.IsTracingRewards;
            set => _isTracingRewards = value;
        }

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
            token.ThrowIfCancellationRequested();
            if (innerTracer.IsTracingRewards)
            {
                innerTracer.ReportReward(author, rewardType, rewardValue);
            }
        }

        public void StartNewBlockTrace(Block block)
        {
            token.ThrowIfCancellationRequested();
            innerTracer.StartNewBlockTrace(block);
        }

        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            token.ThrowIfCancellationRequested();
            ITxTracer txTracer = innerTracer.StartNewTxTrace(tx);
            // Transactions are traced one at a time, so the previous transaction's wrapper is free to reuse.
            if (_txTracer is null) _txTracer = txTracer.WithCancellation(token);
            else _txTracer.Reset(txTracer);
            return _txTracer;
        }

        public void EndTxTrace()
        {
            token.ThrowIfCancellationRequested();
            innerTracer.EndTxTrace();
        }

        public void EndBlockTrace() => innerTracer.EndBlockTrace();
    }
}
