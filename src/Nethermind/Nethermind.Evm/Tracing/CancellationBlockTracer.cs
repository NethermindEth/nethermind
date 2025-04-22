// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.ExecutionWitness;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class CancellationBlockTracer(IBlockTracer innerTracer, CancellationToken token = default) : IBlockTracer
    {
        private bool _isTracingRewards;
        private bool _isTracingVerkleWitness;

        public bool IsTracingRewards
        {
            get => _isTracingRewards || innerTracer.IsTracingRewards;
            set => _isTracingRewards = value;
        }

        public bool IsTracingAccessWitness
        {
            get => _isTracingVerkleWitness || innerTracer.IsTracingAccessWitness;
            set => _isTracingVerkleWitness = value;
        }


        public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
            token.ThrowIfCancellationRequested();
            if (innerTracer.IsTracingRewards)
            {
                innerTracer.ReportReward(author, rewardType, rewardValue);
            }
        }

        public void ReportAccessWitness(IExecutionWitness witness)
        {
            token.ThrowIfCancellationRequested();
            if (innerTracer.IsTracingRewards)
            {
                innerTracer.ReportAccessWitness(witness);
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
            return innerTracer.StartNewTxTrace(tx).WithCancellation(token);
        }

        public void EndTxTrace()
        {
            token.ThrowIfCancellationRequested();
            innerTracer.EndTxTrace();
        }

        public void EndBlockTrace()
        {
            innerTracer.EndBlockTrace();
        }
    }
}
