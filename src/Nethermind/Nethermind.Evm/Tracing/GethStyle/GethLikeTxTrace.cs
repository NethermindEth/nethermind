// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Evm.Tracing.GethStyle.Custom;

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

    public long Gas { get; set; }

    public bool Failed { get; set; }

    public byte[] ReturnValue { get; set; } = Array.Empty<byte>();

    public List<GethTxTraceEntry> Entries { get; set; } = new();

    public GethLikeCustomTrace? CustomTracerResult { get; set; }

    public void Dispose()
    {
        _disposable?.Dispose();
    }
}
