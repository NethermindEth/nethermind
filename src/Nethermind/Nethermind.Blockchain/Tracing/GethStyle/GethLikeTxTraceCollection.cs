// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;

namespace Nethermind.Blockchain.Tracing.GethStyle;

[JsonConverter(typeof(GethLikeTxTraceCollectionConverter))]
public record GethLikeTxTraceCollection(IReadOnlyCollection<GethLikeTxTrace> Traces) : IReadOnlyCollection<GethLikeTxTrace>, IDisposable
{
    public IEnumerator<GethLikeTxTrace> GetEnumerator() => Traces.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int Count => Traces.Count;
    public void Dispose()
    {
        Traces.DisposeItems();
        Traces.TryDispose();
    }
}
