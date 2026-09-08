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

public delegate GethLikeNativeTxTracer GethLikeNativeTracerFactoryDelegate(GethTraceOptions options, Block block, Transaction transaction, IWorldState worldState);

public static class GethLikeNativeTracerFactory
{
    static GethLikeNativeTracerFactory() => RegisterNativeTracers();

    // Built-in tracers may need the active spec (EIP-8037 fork gating); external registrations ignore it.
    private delegate GethLikeNativeTxTracer SpecAwareNativeTracerFactory(GethTraceOptions options, Block block, Transaction transaction, IWorldState worldState, IReleaseSpec releaseSpec);

    private static readonly Dictionary<string, SpecAwareNativeTracerFactory> _tracers = [];

    public static bool IsNativeTracer(string tracerName) => !string.IsNullOrWhiteSpace(tracerName) && _tracers.ContainsKey(tracerName);

    private static void RegisterNativeTracers()
    {
        _tracers.Add(Native4ByteTracer.FourByteTracer, static (options, _, transaction, _, _) => new Native4ByteTracer(transaction, options));
        _tracers.Add(NativePrestateTracer.PrestateTracer, static (options, block, transaction, worldState, _) => new NativePrestateTracer(worldState, options, transaction.Hash, transaction.SenderAddress, transaction.To, block.Beneficiary));
        _tracers.Add(NativeCallTracer.CallTracer, static (options, _, transaction, _, releaseSpec) => new NativeCallTracer(transaction, releaseSpec, options));
        _tracers.Add(NativeStateGasTracer.StateGasTracer, static (options, _, transaction, _, releaseSpec) => new NativeStateGasTracer(transaction, releaseSpec, options));
    }

    public static void RegisterTracer(string tracerName, GethLikeNativeTracerFactoryDelegate tracerDelegate) =>
        _tracers.Add(tracerName, (options, block, transaction, worldState, _) => tracerDelegate(options, block, transaction, worldState));

    public static GethLikeNativeTxTracer CreateTracer(GethTraceOptions options, Block block, Transaction transaction, IWorldState worldState, IReleaseSpec releaseSpec) =>
        _tracers.TryGetValue(options.Tracer, out SpecAwareNativeTracerFactory? tracerDelegate)
            ? tracerDelegate(options, block, transaction, worldState, releaseSpec)
            : throw new ArgumentException($"Unknown tracer: {options.Tracer}");
}
