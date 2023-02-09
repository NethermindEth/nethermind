// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class NullBlockTracer : IBlockTracer
    {
        private static NullBlockTracer _instance;

        private NullBlockTracer()
        {
        }

        public static NullBlockTracer Instance
        {
            get { return LazyInitializer.EnsureInitialized(ref _instance, () => new NullBlockTracer()); }
        }

        public bool IsTracingRewards => false;

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
        }

        public void StartNewBlockTrace(Block block)
        {
        }

        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            return NullTxTracer.Instance;
        }

        public void EndTxTrace()
        {
        }

        public void EndBlockTrace()
        {
        }
    }
}
