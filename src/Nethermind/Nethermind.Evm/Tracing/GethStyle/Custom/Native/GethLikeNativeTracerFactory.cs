// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Tracers;
namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native;

public static class GethLikeNativeTracerFactory
{
    static GethLikeNativeTracerFactory() => RegisterNativeTracers();

    private static readonly Dictionary<string, Func<GethTraceOptions, GethLikeNativeTxTracer>> _tracers = new();

    public static GethLikeNativeTxTracer CreateTracer(GethTraceOptions options) =>
        _tracers.TryGetValue(options.Tracer, out Func<GethTraceOptions, GethLikeNativeTxTracer> tracerFunc)
        ? tracerFunc(options)
        : throw new ArgumentException($"Unknown tracer: {options.Tracer}");

    public static bool IsNativeTracer(string tracerName)
    {
        return _tracers.ContainsKey(tracerName);
    }

    private static void RegisterNativeTracers()
    {
        RegisterTracer(Native4ByteTracer.FourByteTracer, static options => new Native4ByteTracer(options));
    }

    private static void RegisterTracer(string tracerName, Func<GethTraceOptions, GethLikeNativeTxTracer> tracerFunc)
    {
        _tracers.Add(tracerName, tracerFunc);
    }
}
