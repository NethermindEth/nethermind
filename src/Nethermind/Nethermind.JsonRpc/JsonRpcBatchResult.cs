// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.JsonRpc;

public class JsonRpcBatchResult : IJsonRpcBatchResult
{
    private readonly Func<JsonRpcBatchResultAsyncEnumerator, CancellationToken, IAsyncEnumerator<JsonRpcResult.Entry>> _innerEnumeratorFactory;

    public JsonRpcBatchResult(Func<JsonRpcBatchResultAsyncEnumerator, CancellationToken, IAsyncEnumerator<JsonRpcResult.Entry>> innerEnumeratorFactory)
    {
        _innerEnumeratorFactory = innerEnumeratorFactory;
    }

    public JsonRpcBatchResultAsyncEnumerator GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new(_innerEnumeratorFactory, cancellationToken);

    IAsyncEnumerator<JsonRpcResult.Entry> IAsyncEnumerable<JsonRpcResult.Entry>.GetAsyncEnumerator(CancellationToken cancellationToken) =>
        GetAsyncEnumerator(cancellationToken);
}
