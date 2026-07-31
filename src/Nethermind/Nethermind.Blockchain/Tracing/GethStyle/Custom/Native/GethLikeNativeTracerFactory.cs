// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.FourByte;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.StateGas;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;

public delegate GethLikeNativeTxTracer GethLikeNativeTracerFactoryDelegate(GethTraceOptions options, Block block, Transaction transaction, IWorldState worldState, IReleaseSpec releaseSpec);

public static class GethLikeNativeTracerFactory
{
    static GethLikeNativeTracerFactory() => RegisterNativeTracers();

    private static readonly Dictionary<string, GethLikeNativeTracerFactoryDelegate> _tracers = [];

    public static bool IsNativeTracer(string tracerName) => !string.IsNullOrWhiteSpace(tracerName) && _tracers.ContainsKey(tracerName);

    private static void RegisterNativeTracers()
    {
        RegisterTracer(Native4ByteTracer.FourByteTracer, static (options, _, transaction, _, _) => new Native4ByteTracer(transaction, options));
        RegisterTracer(NativePrestateTracer.PrestateTracer, static (options, block, transaction, worldState, _) => new NativePrestateTracer(worldState, options, transaction.Hash, transaction.SenderAddress, transaction.To, block.Beneficiary));
        RegisterTracer(NativeCallTracer.CallTracer, static (options, _, transaction, _, releaseSpec) => new NativeCallTracer(transaction, releaseSpec, options));
        RegisterTracer(NativeStateGasTracer.StateGasTracer, static (options, _, transaction, _, releaseSpec) => new NativeStateGasTracer(transaction, releaseSpec, options));
    }

    public static void RegisterTracer(string tracerName, GethLikeNativeTracerFactoryDelegate tracerDelegate) =>
        _tracers.Add(tracerName, tracerDelegate);

    public static GethLikeNativeTxTracer CreateTracer(GethTraceOptions options, Block block, Transaction transaction, IWorldState worldState, IReleaseSpec releaseSpec) =>
        _tracers.TryGetValue(options.Tracer, out GethLikeNativeTracerFactoryDelegate tracerDelegate)
            ? tracerDelegate(options, block, transaction, worldState, releaseSpec)
            : throw new ArgumentException($"Unknown tracer: {options.Tracer}");
}
