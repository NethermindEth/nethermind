// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.FourByte;
using Nethermind.Evm.Tracing;
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

    [Test]
    public void EndTxTrace_disposes_native_tracer()
    {
        TestNativeTxTracer? startedTracer = null;
        GethLikeBlockNativeTracer blockTracer = new(null, (_, _) =>
        {
            startedTracer = new TestNativeTxTracer();
            return startedTracer;
        });

        ((IBlockTracer)blockTracer).StartNewBlockTrace(_block);
        ((IBlockTracer)blockTracer).StartNewTxTrace(_tx);
        ((IBlockTracer)blockTracer).EndTxTrace();

        Assert.That(startedTracer, Is.Not.Null);
        Assert.That(startedTracer?.Disposed, Is.True);
    }

    [Test]
    public void EndTxTrace_disposes_native_tracer_when_build_result_throws()
    {
        TestNativeTxTracer? startedTracer = null;
        GethLikeBlockNativeTracer blockTracer = new(null, (_, _) =>
        {
            startedTracer = new TestNativeTxTracer(throwOnBuildResult: true);
            return startedTracer;
        });

        ((IBlockTracer)blockTracer).StartNewBlockTrace(_block);
        ((IBlockTracer)blockTracer).StartNewTxTrace(_tx);

        Assert.Throws<InvalidOperationException>(() => ((IBlockTracer)blockTracer).EndTxTrace());
        Assert.That(startedTracer, Is.Not.Null);
        Assert.That(startedTracer?.Disposed, Is.True);
    }

    private sealed class TestNativeTxTracer(bool throwOnBuildResult = false) : GethLikeNativeTxTracer(GethTraceOptions.Default), IDisposable
    {
        public bool Disposed { get; private set; }

        public override GethLikeTxTrace BuildResult()
        {
            if (throwOnBuildResult)
            {
                throw new InvalidOperationException("BuildResult failure");
            }

            return new GethLikeTxTrace();
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
