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
    public class CompositeBlockTracer : IBlockTracer
    {
        private readonly IBlockTracer[] _childTracers;
        public bool IsTracingRewards { get; private set; }

        public CompositeBlockTracer(IBlockTracer[] childTracers)
        {
            _childTracers = childTracers;
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
            foreach (IBlockTracer childTracer in _childTracers)
            {
                if (!childTracer.IsTracingRewards) continue;
                childTracer.ReportReward(author, rewardType, rewardValue);
            }
        }

        public void StartNewBlockTrace(Block block)
        {
            foreach (IBlockTracer childTracer in _childTracers)
            {
                childTracer.StartNewBlockTrace(block);
            }
        }

        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            IBlockTracer[] childBlockTracers = _childTracers;
            if (childBlockTracers.Length == 0) return NullTxTracer.Instance;
            
            ITxTracer[] childTxTracers = childBlockTracers
                .Select(childBlockTracer => childBlockTracer.StartNewTxTrace(tx))
                .Where(childTxTracer => childTxTracer != NullTxTracer.Instance).ToArray();
            return childTxTracers.Any() ? new CompositeTxTracer(childTxTracers) : NullTxTracer.Instance;
        }

        public void EndBlockTrace()
        {
            if (_childTracers.Length == 0) return;
            foreach (IBlockTracer childTracer in _childTracers)
            {
                childTracer.EndBlockTrace();
            }
        }
    }
}
