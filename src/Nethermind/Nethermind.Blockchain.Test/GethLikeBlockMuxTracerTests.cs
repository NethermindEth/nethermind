// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Blockchain.Test;

public class GethLikeBlockMuxTracerTests
{
    [Test]
    public void Rejects_child_trace_count_mismatch([Values(0, 2)] int count)
    {
        CountingResource resource = new();
        Child child = new(count, resource);
        using GethLikeBlockMuxTracer mux = Create(child);
        mux.StartNewTxTrace(Build.A.Transaction.TestObject);
        Assert.That(Assert.Throws<InvalidOperationException>(() => mux.BuildResult())!.Message,
            Is.EqualTo("Mux child returned a different number of transaction traces."));
        Assert.That(resource.Disposals, Is.EqualTo(count));
    }

    [Test]
    public void Releases_traces_before_child_disposal_without_disposing_borrowed_collection([Values] bool buildResult)
    {
        CountingResource resource = new();
        Child child = new(1, resource);
        GethLikeBlockMuxTracer mux = Create(child);
        mux.StartNewTxTrace(Build.A.Transaction.TestObject);
        if (buildResult) mux.BuildResult();
        mux.Dispose();
        mux.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resource.Disposals, Is.EqualTo(1));
            Assert.That(child.Disposals, Is.EqualTo(1));
            Assert.That(child.Results.Disposals, Is.Zero);
        }
    }

    [Test]
    public void Result_failure_still_releases_other_children_and_disposes_once()
    {
        CountingResource resource = new();
        Child failing = new(0, resource) { FailResult = true };
        Child other = new(1, resource);
        GethLikeBlockMuxTracer mux = new(GethTraceOptions.Default with
        {
            TracerConfig = JsonSerializer.Deserialize<JsonElement>("{\"first\":{},\"second\":{}}")
        }, options => options.Tracer == "first" ? failing : other);
        Assert.Throws<InvalidOperationException>(() => mux.Dispose());
        mux.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failing.Disposals, Is.EqualTo(1));
            Assert.That(other.Disposals, Is.EqualTo(1));
            Assert.That(resource.Disposals, Is.EqualTo(1));
            Assert.That(other.Results.Disposals, Is.Zero);
        }
    }

    [Test]
    public void Initialization_failure_survives_result_cleanup_failure([Values] bool constructorFailure, [Values] bool argumentFailure, [Values] bool existingDiagnostic)
    {
        Exception original = argumentFailure ? new ArgumentException("original failure") : new InvalidDataException("original failure");
        object diagnostic = new();
        if (existingDiagnostic) original.Data["MuxCleanupException"] = diagnostic;
        CountingResource resource = new();
        ITxTracer started = Substitute.For<ITxTracer>();
        Child failingResult = new(0, resource) { FailResult = true, StartResult = started };
        Child other = new(1, resource);
        Child failingStart = new(0, resource) { StartFailure = original };
        GethLikeBlockMuxTracer? mux = null;
        Exception? actual = Assert.Catch(() =>
        {
            mux = new GethLikeBlockMuxTracer(GethTraceOptions.Default with
            {
                TracerConfig = JsonSerializer.Deserialize<JsonElement>("{\"first\":{},\"second\":{},\"last\":{}}")
            }, options => options.Tracer switch
            {
                "first" => failingResult,
                "second" => other,
                _ => constructorFailure ? throw original : failingStart
            });
            mux.StartNewTxTrace(Build.A.Transaction.TestObject);
        });
        mux?.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, !constructorFailure && argumentFailure ? Is.TypeOf<InvalidDataException>() : Is.SameAs(original));
            if (!constructorFailure && argumentFailure) Assert.That(actual!.InnerException, Is.SameAs(original));
            Assert.That(actual!.Message, Is.EqualTo(original.Message));
            Assert.That(original.Data["MuxCleanupException"], existingDiagnostic ? Is.SameAs(diagnostic) : Is.TypeOf<InvalidOperationException>());
            Assert.That(resource.Disposals, Is.EqualTo(1));
            Assert.That(failingResult.Disposals, Is.EqualTo(1));
            Assert.That(other.Disposals, Is.EqualTo(1));
            Assert.That(failingStart.Disposals, Is.EqualTo(constructorFailure ? 0 : 1));
            Assert.That(other.Results.Disposals, Is.Zero);
        }
        started.Received(constructorFailure ? 0 : 1).Dispose();
    }

    private static GethLikeBlockMuxTracer Create(Child child) => new(GethTraceOptions.Default with
    {
        TracerConfig = JsonSerializer.Deserialize<JsonElement>("{\"child\":{}}")
    }, _ => child);

    private sealed class CountingResource : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    private sealed class BorrowedResults(GethLikeTxTrace[] traces) : IReadOnlyCollection<GethLikeTxTrace>, IDisposable
    {
        public int Count => traces.Length;
        public int Disposals { get; private set; }
        public IEnumerator<GethLikeTxTrace> GetEnumerator() => ((IEnumerable<GethLikeTxTrace>)traces).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => Disposals++;
    }

    private sealed class Child : IBlockTracer<GethLikeTxTrace>, IDisposable
    {
        public Child(int count, CountingResource resource)
        {
            GethLikeTxTrace[] traces = new GethLikeTxTrace[count];
            for (int i = 0; i < count; i++) traces[i] = new GethLikeTxTrace(resource);
            Results = new BorrowedResults(traces);
        }

        public BorrowedResults Results { get; }
        public bool FailResult { get; init; }
        public Exception? StartFailure { get; init; }
        public ITxTracer StartResult { get; init; } = NullTxTracer.Instance;
        public int Disposals { get; private set; }
        public bool IsTracingRewards => false;
        public void StartNewBlockTrace(Block block) { }
        public ITxTracer StartNewTxTrace(Transaction? tx) => StartFailure is null ? StartResult : throw StartFailure;
        public void EndTxTrace() { }
        public void EndBlockTrace() { }
        public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }
        public IReadOnlyCollection<GethLikeTxTrace> BuildResult()
        {
            ObjectDisposedException.ThrowIf(Disposals != 0, this);
            if (FailResult) throw new InvalidOperationException("result unavailable");
            return Results;
        }
        public void Dispose() => Disposals++;
    }
}
