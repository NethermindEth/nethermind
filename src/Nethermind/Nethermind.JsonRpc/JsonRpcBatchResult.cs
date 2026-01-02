// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.JsonRpc;

public class JsonRpcBatchResult(Func<JsonRpcBatchResultAsyncEnumerator, CancellationToken, IAsyncEnumerator<JsonRpcResult.Entry>> innerEnumeratorFactory)
    : IJsonRpcBatchResult
{
    private Action? _disposableAction;

    public JsonRpcBatchResultAsyncEnumerator GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new(innerEnumeratorFactory, cancellationToken);

    IAsyncEnumerator<JsonRpcResult.Entry> IAsyncEnumerable<JsonRpcResult.Entry>.GetAsyncEnumerator(CancellationToken cancellationToken) =>
        GetAsyncEnumerator(cancellationToken);

    public void AddDisposable(Action disposableAction)
    {
        if (_disposableAction is null)
        {
            _disposableAction = disposableAction;
        }
        else
        {
            _disposableAction += disposableAction;
        }
    }

    public void Dispose()
    {
        _disposableAction?.Invoke();
        _disposableAction = null;
    }
}
