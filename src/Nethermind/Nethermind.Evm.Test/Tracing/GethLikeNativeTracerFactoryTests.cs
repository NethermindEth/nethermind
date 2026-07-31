// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.FourByte;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.StateGas;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeNativeTracerFactoryTests
{
    private readonly Block _block = Build.A.Block.TestObject;
    private readonly Transaction _tx = Build.A.Transaction.TestObject;

    [Test]
    public void CreateTracer_NativeTracerExists()
    {
        GethTraceOptions options = new() { Tracer = Native4ByteTracer.FourByteTracer };

        GethLikeNativeTxTracer? nativeTracer = GethLikeNativeTracerFactory.CreateTracer(options, _block, _tx, null!, null!);

        Assert.That(nativeTracer is Native4ByteTracer, Is.True);
    }

    [Test]
    public void CreateTracer_StateGasTracerExists()
    {
        GethTraceOptions options = new() { Tracer = NativeStateGasTracer.StateGasTracer };
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();

        GethLikeNativeTxTracer? nativeTracer = GethLikeNativeTracerFactory.CreateTracer(options, _block, _tx, null!, spec);

        Assert.That(nativeTracer is NativeStateGasTracer, Is.True);
    }

    [Test]
    public void CreateTracer_NativeTracerDoesNotExist()
    {
        GethTraceOptions options = new() { Tracer = "nonExistentTracer" };

        Assert.Throws<ArgumentException>(() => GethLikeNativeTracerFactory.CreateTracer(options, _block, _tx, null!, null!));
    }

    [Test]
    public void IsNativeTracer_TracerNameExists()
    {
        bool isNativeTracer = GethLikeNativeTracerFactory.IsNativeTracer(Native4ByteTracer.FourByteTracer);

        Assert.That(isNativeTracer, Is.True);
    }

    [Test]
    public void IsNativeTracer_TracerNameDoesNotExist()
    {
        bool isNativeTracer = GethLikeNativeTracerFactory.IsNativeTracer("nonExistentTracer");

        Assert.That(isNativeTracer, Is.False);
    }

    [Test]
    public void CreateTracer_TracerNameIsEmpty()
    {
        bool isNativeTracer = GethLikeNativeTracerFactory.IsNativeTracer(string.Empty);

        Assert.That(isNativeTracer, Is.False);
    }

    [Test]
    public void CreateTracer_TracerNameIsNull()
    {
        bool isNativeTracer = GethLikeNativeTracerFactory.IsNativeTracer(null);

        Assert.That(isNativeTracer, Is.False);
    }
}
