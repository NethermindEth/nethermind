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
        private readonly ImmutableArray<IBlockTracer> _childTracers;
        public bool IsTracingRewards { get; private set; }

        public CompositeBlockTracer(IEnumerable<IBlockTracer> childTracers)
        {
            _childTracers = ImmutableArray<IBlockTracer>.Empty.AddRange(childTracers);
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

        public ITxTracer StartNewTxTrace(Keccak? txHash)
        {
            ImmutableArray<IBlockTracer> childBlockTracers = _childTracers;
            if (childBlockTracers.Length == 0) return NullTxTracer.Instance;
            
            IEnumerable<ITxTracer> childTxTracers = childBlockTracers
                .Select(childBlockTracer => childBlockTracer.StartNewTxTrace(txHash))
                .Where(childTxTracer => childTxTracer != NullTxTracer.Instance);
            return childTxTracers.Any() ? new CompositeTxTracer(childTxTracers.ToArray()) : NullTxTracer.Instance;
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
