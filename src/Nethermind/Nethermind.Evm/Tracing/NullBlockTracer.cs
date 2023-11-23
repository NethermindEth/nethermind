// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;

namespace Nethermind.Evm.Tracing
{
    public class NullBlockTracer : BlockTracer
    {
        private static NullBlockTracer? _instance;
        public static NullBlockTracer Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new NullBlockTracer());
        public override ITxTracer StartNewTxTrace(Transaction? tx) => NullTxTracer.Instance;
    }
}
