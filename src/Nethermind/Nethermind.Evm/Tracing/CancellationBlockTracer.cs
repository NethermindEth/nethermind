// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.Witness;
using Nethermind.Int256;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Evm.Tracing
{
    public class CancellationBlockTracer : IBlockTracer
    {
        private readonly IBlockTracer _innerTracer;
        private readonly CancellationToken _token;
        private bool _isTracingRewards;
        private bool _isTracingVerkleWitness;

        public CancellationBlockTracer(IBlockTracer innerTracer, CancellationToken token = default)
        {
            _innerTracer = innerTracer;
            _token = token;
        }

        public bool IsTracingRewards
        {
            get => _isTracingRewards || _innerTracer.IsTracingRewards;
            set => _isTracingRewards = value;
        }

        public bool IsTracingAccessWitness
        {
            get => _isTracingVerkleWitness || _innerTracer.IsTracingAccessWitness;
            set => _isTracingVerkleWitness = value;
        }


        public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingRewards)
            {
                _innerTracer.ReportReward(author, rewardType, rewardValue);
            }
        }

        public void ReportAccessWitness(IExecutionWitness witness)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingRewards)
            {
                _innerTracer.ReportAccessWitness(witness);
            }
        }

        public void StartNewBlockTrace(Block block)
        {
            _token.ThrowIfCancellationRequested();
            _innerTracer.StartNewBlockTrace(block);
        }

        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            _token.ThrowIfCancellationRequested();
            return _innerTracer.StartNewTxTrace(tx).WithCancellation(_token);
        }

        public void EndTxTrace()
        {
            _token.ThrowIfCancellationRequested();
            _innerTracer.EndTxTrace();
        }

        public void EndBlockTrace()
        {
            _innerTracer.EndBlockTrace();
        }
    }
}
