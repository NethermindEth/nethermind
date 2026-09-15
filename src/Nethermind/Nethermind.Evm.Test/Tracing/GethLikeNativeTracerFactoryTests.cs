// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.FourByte;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Noop;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.StateGas;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeNativeTracerFactoryTests
{
    private readonly Block _block = Build.A.Block.TestObject;
    private readonly Transaction _tx = Build.A.Transaction.TestObject;

    [TestCase(Native4ByteTracer.FourByteTracer, typeof(Native4ByteTracer))]
    [TestCase(NativeNoopTracer.NoopTracer, typeof(NativeNoopTracer))]
    [TestCase(NativeStateGasTracer.StateGasTracer, typeof(NativeStateGasTracer))]
    public void CreateTracer_NativeTracerExists(string tracerName, Type expectedTracer)
    {
        GethTraceOptions options = new() { Tracer = tracerName };

        GethLikeNativeTxTracer nativeTracer = GethLikeNativeTracerFactory.CreateTracer(options, _block, _tx, null!, Substitute.For<IReleaseSpec>());

        Assert.That(nativeTracer, Is.InstanceOf(expectedTracer));
    }

    [Test]
    public void CreateTracer_NoopTracer_TracesNothingButTheReceipt()
    {
        GethTraceOptions options = new() { Tracer = NativeNoopTracer.NoopTracer };

        GethLikeNativeTxTracer nativeTracer = GethLikeNativeTracerFactory.CreateTracer(options, _block, _tx, null!, Substitute.For<IReleaseSpec>());

        Assert.Multiple(() =>
        {
            Assert.That(nativeTracer.IsTracingInstructions, Is.False);
            Assert.That(nativeTracer.IsTracingActions, Is.False);
            Assert.That(nativeTracer.IsTracingStack, Is.False);
            Assert.That(nativeTracer.IsTracingMemory, Is.False);
            Assert.That(nativeTracer.IsTracingOpLevelStorage, Is.False);
            Assert.That(nativeTracer.IsTracingReceipt, Is.True);
        });
    }

    [Test]
    public void CreateTracer_NativeTracerDoesNotExist()
    {
        GethTraceOptions options = new() { Tracer = "nonExistentTracer" };

        Assert.Throws<ArgumentException>(() => GethLikeNativeTracerFactory.CreateTracer(options, _block, _tx, null!, Substitute.For<IReleaseSpec>()));
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
