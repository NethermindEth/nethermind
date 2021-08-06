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
// 

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class CancellationBlockTracer : IBlockTracer
    {
        private readonly IBlockTracer _innerTracer;
        private readonly CancellationToken _token;
        private bool _isTracingRewards;

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

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingRewards)
            {
                _innerTracer.ReportReward(author, rewardType, rewardValue);
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
