// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.FourByte;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeNativeTracerFactoryTests
{
    private readonly Block _block = Build.A.Block.TestObject;
    private readonly Transaction _tx = Build.A.Transaction.TestObject;

    [Test]
    public void CreateTracer_NativeTracerExists()
    {
        var options = new GethTraceOptions { Tracer = Native4ByteTracer.FourByteTracer };

        GethLikeNativeTxTracer? nativeTracer = GethLikeNativeTracerFactory.CreateTracer(options, _block, _tx, null!);

        Assert.That(nativeTracer is Native4ByteTracer, Is.True);
    }

    [Test]
    public void CreateTracer_NativeTracerDoesNotExist()
    {
        var options = new GethTraceOptions { Tracer = "nonExistentTracer" };

        Assert.Throws<ArgumentException>(() => GethLikeNativeTracerFactory.CreateTracer(options, _block, _tx, null!));
    }

    [Test]
    public void IsNativeTracer_TracerNameExists()
    {
        var isNativeTracer = GethLikeNativeTracerFactory.IsNativeTracer(Native4ByteTracer.FourByteTracer);

        Assert.That(isNativeTracer, Is.True);
    }

    [Test]
    public void IsNativeTracer_TracerNameDoesNotExist()
    {
        var isNativeTracer = GethLikeNativeTracerFactory.IsNativeTracer("nonExistentTracer");

        Assert.That(isNativeTracer, Is.False);
    }

    [Test]
    public void CreateTracer_TracerNameIsEmpty()
    {
        var isNativeTracer = GethLikeNativeTracerFactory.IsNativeTracer(string.Empty);

        Assert.That(isNativeTracer, Is.False);
    }

    [Test]
    public void CreateTracer_TracerNameIsNull()
    {
        var isNativeTracer = GethLikeNativeTracerFactory.IsNativeTracer(null);

        Assert.That(isNativeTracer, Is.False);
    }
}
