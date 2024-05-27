// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.FourByte;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native;

public delegate GethLikeNativeTxTracer GethLikeNativeTracerFactoryDelegate(GethTraceOptions options, Block block, Transaction transaction, IWorldState worldState);

public static class GethLikeNativeTracerFactory
{
    static GethLikeNativeTracerFactory() => RegisterNativeTracers();

    private static readonly Dictionary<string, GethLikeNativeTracerFactoryDelegate> _tracers = new();

    public static bool IsNativeTracer(string tracerName)
    {
        return !string.IsNullOrWhiteSpace(tracerName) && _tracers.ContainsKey(tracerName);
    }

    private static void RegisterNativeTracers()
    {
        RegisterTracer(Native4ByteTracer.FourByteTracer, (options, _, _, _) => new Native4ByteTracer(options));
        RegisterTracer(NativePrestateTracer.PrestateTracer, (options, block, transaction, worldState) => new NativePrestateTracer(worldState, options, transaction.SenderAddress, transaction.To, block.Beneficiary));
        RegisterTracer(NativeCallTracer.CallTracer, (options, _, transaction, _) => new NativeCallTracer(transaction, options));
    }

    private static void RegisterTracer(string tracerName, GethLikeNativeTracerFactoryDelegate tracerDelegate)
    {
        _tracers.Add(tracerName, tracerDelegate);
    }

    public static GethLikeNativeTxTracer CreateTracer(GethTraceOptions options, Block block, Transaction transaction, IWorldState worldState) =>
        _tracers.TryGetValue(options.Tracer, out GethLikeNativeTracerFactoryDelegate tracerDelegate)
            ? tracerDelegate(options, block, transaction, worldState)
            : throw new ArgumentException($"Unknown tracer: {options.Tracer}");
}
