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

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Execution
{
    public class TxBundleSimulator : TxBundleExecutor<SimulatedMevBundle, TxBundleSimulator.BundleTracer>, IBundleSimulator
    {
        private readonly ITimestamper _timestamper;
        private readonly CancellationToken _cancellationToken;
        private CancellationTokenSource _cancellationTokenSource = null!;
        private long _gasLimit;

        public TxBundleSimulator(ITracerFactory tracerFactory, ITimestamper timestamper, CancellationToken cancellationToken) : base(tracerFactory)
        {
            _timestamper = timestamper;
            _cancellationToken = cancellationToken;
        }

        public SimulatedMevBundle Simulate(MevBundle bundle, BlockHeader parent, long gasLimit)
        {
            _gasLimit = gasLimit;
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
            try
            {
                return ExecuteBundle(bundle, parent, _cancellationTokenSource.Token, _timestamper.UnixTime.Seconds);
            }
            catch (OperationCanceledException)
            {
                return new SimulatedMevBundle(bundle, 0, UInt256.Zero, UInt256.Zero);
            }
            finally
            {
                _cancellationTokenSource.Dispose();
            }
        }

        protected override SimulatedMevBundle BuildResult(MevBundle bundle, Block block, BundleTracer tracer, Keccak resultStateRoot) => 
            new(bundle, tracer.GasUsed, tracer.TxFees, tracer.CoinbasePayments);

        protected override BundleTracer CreateBlockTracer() => new(_gasLimit, Beneficiary);

        public class BundleTracer : IBlockTracer
        {
            private readonly long _gasLimit;
            private readonly Address _beneficiary;
            public long GasUsed { get; private set; }

            public BundleTracer(long gasLimit, Address beneficiary)
            {
                _gasLimit = gasLimit;
                _beneficiary = beneficiary;
            }

            private CallOutputTracer? _tracer;
            
            public bool IsTracingRewards => true;
            public UInt256 TxFees { get; private set; }
            public UInt256 CoinbasePayments { get; private set; }
            
            public UInt256 Reward { get; private set; }

            public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
            {
                if (author == _beneficiary)
                {
                    Reward = rewardValue;
                }
            }

            public void StartNewBlockTrace(Block block) { }
            public ITxTracer StartNewTxTrace(Keccak? txHash) => _tracer = new();

            public void EndTxTrace()
            {
                GasUsed += _tracer!.GasSpent;
                if (GasUsed > _gasLimit)
                {
                    throw new OperationCanceledException("Block gas limit exceeded.");
                }
            }
        }
    }
}
