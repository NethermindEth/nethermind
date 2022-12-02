// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public abstract class BlockTracerBase<TTrace, TTracer> : IBlockTracer where TTracer : class, ITxTracer
    {
        private readonly Keccak? _txHash;

        private bool IsTracingEntireBlock => _txHash is null;

        protected BlockTracerBase()
        {
            TxTraces = new List<TTrace>();
        }

        protected BlockTracerBase(Keccak? txHash)
        {
            _txHash = txHash;
            TxTraces = new List<TTrace>();
        }

        private TTracer? CurrentTxTracer { get; set; }

        protected abstract TTracer OnStart(Transaction? tx);
        protected abstract TTrace OnEnd(TTracer txTracer);

        public virtual bool IsTracingRewards => false;

        public virtual void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
        }

        public abstract void StartNewBlockTrace(Block block);

        ITxTracer IBlockTracer.StartNewTxTrace(Transaction? tx)
        {
            if (ShouldTraceTx(tx))
            {
                CurrentTxTracer = OnStart(tx);
                return CurrentTxTracer;
            }

            return NullTxTracer.Instance;
        }

        void IBlockTracer.EndTxTrace()
        {
            if (CurrentTxTracer is not null)
            {
                TxTraces.Add(OnEnd(CurrentTxTracer));
                CurrentTxTracer = null;
            }
        }

        public abstract void EndBlockTrace();

        protected virtual bool ShouldTraceTx(Transaction? tx)
        {
            return IsTracingEntireBlock || tx?.Hash == _txHash;
        }

        protected List<TTrace> TxTraces { get; }

        public IReadOnlyCollection<TTrace> BuildResult()
        {
            return TxTraces;
        }
    }
}
