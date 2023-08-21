// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;

namespace Nethermind.Evm.Tracing
{
    public class AlwaysCancelBlockTracer : BlockTracer
    {
        private static AlwaysCancelBlockTracer? _instance;

        private AlwaysCancelBlockTracer()
        {
        }

        public static AlwaysCancelBlockTracer Instance
        {
            get { return LazyInitializer.EnsureInitialized(ref _instance, () => new AlwaysCancelBlockTracer()); }
        }

        public override bool IsTracingRewards => true;

        public override ITxTracer StartNewTxTrace(Transaction? tx)
        {
            return AlwaysCancelTxTracer.Instance;
        }
    }
}
