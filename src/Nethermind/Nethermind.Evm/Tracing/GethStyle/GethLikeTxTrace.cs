// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle.Custom;
using Nethermind.Serialization.Json;

namespace Nethermind.Evm.Tracing.GethStyle;

[JsonConverter(typeof(GethLikeTxTraceConverter))]
public class GethLikeTxTrace : IDisposable
{
    private readonly IDisposable? _disposable;

    public GethLikeTxTrace(IDisposable? disposable = null)
    {
        _disposable = disposable;
    }

    public GethLikeTxTrace() { }

    public Stack<Dictionary<string, string>> StoragesByDepth { get; } = new();

    [JsonIgnore]
    public Hash256 TransactionHash { get; set; } // here simply for debug_traceblock endpoint.

    public long Gas { get; set; }

    public bool Failed { get; set; }

    public byte[] ReturnValue { get; set; } = [];

    public List<GethTxTraceEntry> Entries { get; set; } = new();

    public GethLikeCustomTrace? CustomTracerResult { get; set; }

    public GethLikeTxTraceResponseDebugTraceBlock DebugTraceBlockResponse()
    {
        return new GethLikeTxTraceResponseDebugTraceBlock(this);
    }

    public void Dispose()
    {
        _disposable?.Dispose();
    }
}

public class GethLikeTxTraceResultDebugTraceBlock
// doing this instead of making a change to the above structure because of the ripple effect to other endpoints!
// if that's preferred then all the endpoints responses would have to be changed - prefered option and initial route I took
// before switching to this, as it was time-consuming and would have required a lot of external input from team memberss!
{

    public GethLikeTxTraceResultDebugTraceBlock(GethLikeTxTrace trace)
    {
        Gas = trace.Gas;
        Failed = trace.Failed;
        ReturnValue = trace.ReturnValue;
        Entries = trace.Entries;
        CustomTracerResult = trace.CustomTracerResult;
    }

    public GethLikeTxTraceResultDebugTraceBlock() { }

    [JsonConverter(typeof(LongRawJsonConverter))]
    public long Gas { get; set; }

    public bool Failed { get; set; }

    public byte[] ReturnValue { get; set; } = Array.Empty<byte>();

    public List<GethTxTraceEntry> Entries { get; set; } = new();

    public GethLikeCustomTrace? CustomTracerResult { get; set; }
}

public class GethLikeTxTraceResponseDebugTraceBlock
{
    [JsonPropertyName("txHash")]
    public Hash256 TransactionHash { get; set; }
    public GethLikeTxTraceResultDebugTraceBlock Result { get; set; }

    public GethLikeTxTraceResponseDebugTraceBlock() { }

    public GethLikeTxTraceResponseDebugTraceBlock(GethLikeTxTrace trace)
    {
        Result = new(trace);
        TransactionHash = trace.TransactionHash;
    }

}
