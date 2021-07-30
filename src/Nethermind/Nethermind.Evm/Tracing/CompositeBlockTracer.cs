//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
