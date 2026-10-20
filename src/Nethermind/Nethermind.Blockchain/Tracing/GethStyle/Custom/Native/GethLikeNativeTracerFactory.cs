// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.FourByte;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Noop;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Preimage;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.StateGas;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Serialization.Json;

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
        _tracers.Add(NativeKeccakPreimageTracer.KeccakPreimageTracer, static (options, _, transaction, _, _) => new NativeKeccakPreimageTracer(transaction, options));
        _tracers.Add(NativeNoopTracer.NoopTracer, static (options, _, transaction, _, _) => new NativeNoopTracer(transaction, options));
        _tracers.Add(NativePrestateTracer.PrestateTracer, static (options, block, transaction, worldState, releaseSpec) => new NativePrestateTracer(worldState, options, transaction.Hash, transaction.SenderAddress, transaction.To, block.Beneficiary, transaction, releaseSpec, transaction.AuthorizationList));
        _tracers.Add(NativeCallTracer.CallTracer, static (options, _, transaction, _, releaseSpec) => new NativeCallTracer(transaction, releaseSpec, options));
        _tracers.Add(NativeStateGasTracer.StateGasTracer, static (options, _, transaction, _, releaseSpec) => new NativeStateGasTracer(transaction, releaseSpec, options));
    }

    public static void RegisterTracer(string tracerName, GethLikeNativeTracerFactoryDelegate tracerDelegate) =>
        _tracers.Add(tracerName, (options, block, transaction, worldState, _) => tracerDelegate(options, block, transaction, worldState));

    internal static T? ParseConfig<T>(JsonElement? config, JsonSerializerOptions options) where T : class
    {
        try
        {
            return TypeInfoJsonSerializer.Deserialize<T>(config, options);
        }
        catch (JsonException exception)
        {
            throw new NativeTracerConfigException(exception);
        }
    }

    public static GethLikeNativeTxTracer CreateTracer(GethTraceOptions options, Block block, Transaction transaction, IWorldState worldState, IReleaseSpec releaseSpec)
    {
        if (!_tracers.TryGetValue(options.Tracer, out SpecAwareNativeTracerFactory? tracerDelegate))
            throw new ArgumentException($"Unknown tracer: {options.Tracer}");

        GethLikeNativeTxTracer tracer = tracerDelegate(options, block, transaction, worldState, releaseSpec);
        try
        {
            options.ResolveTimeout();
            return tracer;
        }
        catch
        {
            tracer.Dispose();
            throw;
        }
    }

    /// <summary>Throws if a built-in tracer cannot parse the tracer config of <paramref name="options"/>.</summary>
    /// <remarks>The tracers parse it per transaction, too late for a streamed response to fail as a JSON-RPC error.</remarks>
    internal static void ValidateTracerConfig(GethTraceOptions options)
    {
        switch (options.Tracer)
        {
            case NativeCallTracer.CallTracer:
                NativeCallTracerConfig.FromJson(options.TracerConfig, EthereumJsonSerializer.JsonOptions);
                break;
            case NativePrestateTracer.PrestateTracer:
                NativePrestateTracerConfig.FromJson(options.TracerConfig, EthereumJsonSerializer.JsonOptions);
                break;
        }
    }
}
