// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using NSubstitute.Core;

namespace Nethermind.JsonRpc.Test;

/// <summary>
/// A processor for two pipelined requests, "slow" and "fast": the slow one is held on a gate, and every start and
/// answer is recorded in order.
/// </summary>
public sealed class PipelinedRequestRecorder
{
    public static readonly string[] InOrder = ["slow start", "slow end", "fast start", "fast end"];

    private readonly List<string> _events = [];
    private readonly Task _slowGate;
    private readonly TaskCompletionSource _slowAnswered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _fastAnswered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PipelinedRequestRecorder(Task slowGate)
    {
        _slowGate = slowGate;
        Processor = Substitute.For<IJsonRpcProcessor>();
        Processor
            .ProcessAsync(
                Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<JsonRpcContext>(),
                Arg.Any<IJsonRpcResponseSink>(),
                Arg.Any<JsonRpcProcessingOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(Process);
    }

    public IJsonRpcProcessor Processor { get; }
    public Task SlowAnswered => _slowAnswered.Task;
    public Task FastAnswered => _fastAnswered.Task;

    public string[] Events
    {
        get
        {
            lock (_events) return [.. _events];
        }
    }

    private async ValueTask Process(CallInfo call)
    {
        bool slow = call.ArgAt<ReadOnlyMemory<byte>>(0).Span.SequenceEqual("slow"u8);
        string name = slow ? "slow" : "fast";

        Record($"{name} start");
        if (slow) await _slowGate;

        IJsonRpcResponseSink sink = call.Arg<IJsonRpcResponseSink>();
        await sink.WriteSingleAsync(new JsonRpcSuccessResponse(null), new RpcReport(), call.Arg<CancellationToken>());
        Record($"{name} end");
        (slow ? _slowAnswered : _fastAnswered).TrySetResult();
    }

    private void Record(string entry)
    {
        lock (_events) _events.Add(entry);
    }
}
