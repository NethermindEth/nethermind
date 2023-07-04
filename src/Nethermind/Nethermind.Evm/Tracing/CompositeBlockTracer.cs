// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class CompositeBlockTracer : IBlockTracer, ITracerBag
    {
        private readonly List<IBlockTracer> _childTracers = new List<IBlockTracer>();
        public bool IsTracingRewards { get; private set; }

        public CompositeBlockTracer()
        {
            IsTracingRewards = _childTracers.Any(childTracer => childTracer.IsTracingRewards);
        }

        public void EndTxTrace()
        {
            foreach (IBlockTracer childTracer in _childTracers)
            {
                childTracer.EndTxTrace();
            }
        }

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
            for (int index = 0; index < _childTracers.Count; index++)
            {
                IBlockTracer childTracer = _childTracers[index];
                if (childTracer.IsTracingRewards)
                {
                    childTracer.ReportReward(author, rewardType, rewardValue);
                }
            }
        }

        public void StartNewBlockTrace(Block block)
        {
            for (int index = 0; index < _childTracers.Count; index++)
            {
                IBlockTracer childTracer = _childTracers[index];
                childTracer.StartNewBlockTrace(block);
            }
        }

        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            IList<IBlockTracer> childBlockTracers = _childTracers;

            List<ITxTracer> tracers = new(childBlockTracers.Count);

            for (int i = 0; i < childBlockTracers.Count; i++)
            {
                IBlockTracer childBlockTracer = childBlockTracers[i];
                ITxTracer txTracer = childBlockTracer.StartNewTxTrace(tx);
                if (txTracer != NullTxTracer.Instance)
                {
                    tracers.Add(txTracer);
                }
            }

            return tracers.Count > 0 ? new CompositeTxTracer(tracers) : NullTxTracer.Instance;
        }

        public void EndBlockTrace()
        {
            for (int index = 0; index < _childTracers.Count; index++)
            {
                IBlockTracer childTracer = _childTracers[index];
                childTracer.EndBlockTrace();
            }
        }

        public void Add(IBlockTracer tracer)
        {
            _childTracers.Add(tracer);
            IsTracingRewards |= tracer.IsTracingRewards;
        }

        public void AddRange(params IBlockTracer[] tracers)
        {
            _childTracers.AddRange(tracers);
            IsTracingRewards |= tracers.Any(t => t.IsTracingRewards);
        }

        public void Remove(IBlockTracer tracer)
        {
            _childTracers.Remove(tracer);
            IsTracingRewards = _childTracers.Any(t => t.IsTracingRewards);

        }

        public IBlockTracer GetTracer() =>
            _childTracers.Count switch
            {
                0 => NullBlockTracer.Instance,
                1 => _childTracers[0],
                _ => this
            };
    }
}
