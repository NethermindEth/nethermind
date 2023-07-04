// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class AlwaysCancelBlockTracer : IBlockTracer
    {
        private static AlwaysCancelBlockTracer _instance;

        private AlwaysCancelBlockTracer()
        {
        }

        public static AlwaysCancelBlockTracer Instance
        {
            get { return LazyInitializer.EnsureInitialized(ref _instance, () => new AlwaysCancelBlockTracer()); }
        }

        public bool IsTracingRewards => true;

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
        }

        public void StartNewBlockTrace(Block block)
        {
        }

        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            return AlwaysCancelTxTracer.Instance;
        }

        public void EndTxTrace()
        {
        }

        public void EndBlockTrace()
        {
        }
    }
}
