// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Blockchain.Tracing;

public class AlwaysCancelBlockTracer : BlockTracer
{
    private static AlwaysCancelBlockTracer? _instance;

    private AlwaysCancelBlockTracer()
    {
    }

    public static AlwaysCancelBlockTracer Instance
    {
        get { return LazyInitializer.EnsureInitialized(ref _instance, static () => new AlwaysCancelBlockTracer()); }
    }

    public override bool IsTracingRewards => true;

    public override ITxTracer StartNewTxTrace(Transaction? tx)
    {
        return AlwaysCancelTxTracer.Instance;
    }
}
