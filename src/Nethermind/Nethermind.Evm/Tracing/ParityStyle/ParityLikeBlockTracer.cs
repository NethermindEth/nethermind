// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    public class ParityLikeBlockTracer : BlockTracerBase<ParityLikeTxTrace, ParityLikeTxTracer>
    {
        private readonly IDictionary<Keccak, ParityTraceTypes>? _typesByTransaction;
        private Block _block;
        private readonly ParityTraceTypes _types;

        public ParityLikeBlockTracer(Keccak txHash, ParityTraceTypes types)
            : base(txHash)
        {
            _types = types;
            IsTracingRewards = (types & ParityTraceTypes.Rewards) == ParityTraceTypes.Rewards;
        }

        public ParityLikeBlockTracer(ParityTraceTypes types)
        {
            _types = types;
            IsTracingRewards = (types & ParityTraceTypes.Rewards) == ParityTraceTypes.Rewards;
        }

        public ParityLikeBlockTracer(IDictionary<Keccak, ParityTraceTypes> typesByTransaction)
        {
            _typesByTransaction = typesByTransaction;
            IsTracingRewards = false;
        }

        protected override ParityLikeTxTracer OnStart(Transaction? tx) => new(_block, tx,
            tx is not null && _typesByTransaction?.TryGetValue(tx.Hash!, out ParityTraceTypes types) == true ? types : _types);

        protected override ParityLikeTxTrace OnEnd(ParityLikeTxTracer txTracer) => txTracer.BuildResult();

        public override bool IsTracingRewards { get; }

        public override void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
            ParityLikeTxTrace rewardTrace = TxTraces.LastOrDefault();
            if (rewardTrace is not null)
            {
                rewardTrace.Action = new ParityTraceAction
                {
                    RewardType = rewardType,
                    Value = rewardValue,
                    Author = author,
                    CallType = "reward",
                    TraceAddress = Array.Empty<int>(),
                    Type = "reward",
                    Result = null
                };
            }
        }

        public override void StartNewBlockTrace(Block block)
        {
            _block = block;
            base.StartNewBlockTrace(block);
        }
    }
}
