// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Tracers;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native;

public static class GethLikeNativeTracerFactory
{
    private const string _4byteTracer = "4byteTracer";

    public static GethLikeNativeTxTracer CreateNativeTracer(
        IReleaseSpec spec,
        GethTraceOptions options)
    {
        // TODO: add static dictionary with register method here instead of hardcoded values
        return options.Tracer switch
        {
            _4byteTracer => new Native4ByteTracer(spec, options),
            _ => throw new ArgumentException($"Unknown tracer: {options.Tracer}")
        };
    }

    public static bool IsNativeTracer(string tracer)
    {
        return tracer is _4byteTracer;
    }
}
