// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native;
namespace Nethermind.Evm.Test.Tracing;

public class GethLikeNativeTracerTestsBase : VirtualMachineTestsBase
{
    protected GethLikeTxTrace ExecuteAndTrace(string tracerName, params byte[] code)
    {
        GethLikeNativeTxTracer tracer = GethLikeNativeTracerFactory.CreateTracer(GethTraceOptions.Default with { Tracer = tracerName });
        (Block block, Transaction transaction) = PrepareTx(Activation, 100000, code);
        _processor.Execute(transaction, block.Header, tracer);
        return tracer.BuildResult();
    }
}
