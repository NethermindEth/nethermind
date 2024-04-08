// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeNativeTracerTestsBase : VirtualMachineTestsBase
{
    protected GethLikeTxTrace ExecuteAndTrace(
        string tracerName,
        byte[] code,
        IWorldState? worldState,
        NativeTracerContext? context,
        byte[]? input = default,
        UInt256 value = default)
    {
        GethLikeNativeTxTracer tracer = GethLikeNativeTracerFactory.CreateTracer(
            worldState,
            context ?? new NativeTracerContext(),
            GethTraceOptions.Default with { Tracer = tracerName });
        (Block block, Transaction transaction) = input is null ? PrepareTx(Activation, 100000, code) : PrepareTx(Activation, 100000, code, input, value);
        _processor.Execute(transaction, block.Header, tracer);
        return tracer.BuildResult();
    }
}
