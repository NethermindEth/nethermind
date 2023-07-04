// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class BlockCallOutputTracer : IBlockTracer
    {
        private readonly Dictionary<Keccak, CallOutputTracer> _results = new();
        public bool IsTracingRewards => false;
        public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }
        public void StartNewBlockTrace(Block block) { }
        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            return _results[tx?.Hash ?? Keccak.Zero] = new CallOutputTracer();
        }

        public void EndTxTrace() { }
        public void EndBlockTrace() { }
        public IReadOnlyDictionary<Keccak, CallOutputTracer> BuildResults() => _results;
    }
}
