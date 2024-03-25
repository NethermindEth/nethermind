// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Tracers;
using NUnit.Framework;
namespace Nethermind.Evm.Test.Tracing;

public class GethLikeNativeTracerFactoryTests
{
    [Test]
    public void CreateTracer_NativeTracerExists()
    {
        var options = new GethTraceOptions { Tracer = Native4ByteTracer._4byteTracer };

        GethLikeNativeTxTracer? nativeTracer = GethLikeNativeTracerFactory.CreateTracer(options);

        Assert.True(nativeTracer is Native4ByteTracer);
    }

    [Test]
    public void CreateTracer_NativeTracerDoesNotExist()
    {
        var options = new GethTraceOptions { Tracer = "nonExistentTracer" };

        Assert.Throws<ArgumentException>(() => GethLikeNativeTracerFactory.CreateTracer(options));
    }

    [Test]
    public void IsNativeTracer_TracerNameExists()
    {
        var isNativeTracer = GethLikeNativeTracerFactory.IsNativeTracer(Native4ByteTracer._4byteTracer);

        Assert.True(isNativeTracer);
    }

    [Test]
    public void IsNativeTracer_TracerNameDoesNotExist()
    {
        var isNativeTracer = GethLikeNativeTracerFactory.IsNativeTracer("nonExistentTracer");

        Assert.False(isNativeTracer);
    }
}
