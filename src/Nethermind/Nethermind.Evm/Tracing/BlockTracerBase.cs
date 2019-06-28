/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Tracing
{
    public abstract class BlockTracerBase<TTrace, TTracer> : IBlockTracer where TTracer : class, ITxTracer
    {
        private readonly Keccak _txHash;

        private bool _isTracingEntireBlock;

        protected BlockTracerBase()
        {
            _isTracingEntireBlock = true;
            TxTraces = new List<TTrace>();
        }

        protected BlockTracerBase(Keccak txHash)
        {
            _txHash = txHash;
            TxTraces = new List<TTrace>();
        }

        protected TTracer CurrentTxTracer { get; set; }

        protected abstract TTracer OnStart(Keccak txHash);
        protected abstract TTrace OnEnd(TTracer txTracer);

        public virtual bool IsTracingRewards => false;

        public virtual void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
        }

        public abstract void StartNewBlockTrace(Block block);

        ITxTracer IBlockTracer.StartNewTxTrace(Keccak txHash)
        {
            if (_isTracingEntireBlock || _txHash == txHash)
            {
                CurrentTxTracer = OnStart(txHash);
                return CurrentTxTracer;
            }
            
            // if(!_isTracingEntireBlock && _txHash != txHash)
            // {
            //     throw new InvalidOperationException($"Unexpected tx trace started - awaiting {_txHash}, received {txHash}");
            // }

            return NullTxTracer.Instance;
        }

        void IBlockTracer.EndTxTrace()
        {
            if (CurrentTxTracer == null)
            {
                return;
            }
                
            TxTraces.Add(OnEnd(CurrentTxTracer));
            CurrentTxTracer = null;
        }

        protected List<TTrace> TxTraces { get; }

        public IReadOnlyCollection<TTrace> BuildResult()
        {
            return TxTraces;
        }
    }
}