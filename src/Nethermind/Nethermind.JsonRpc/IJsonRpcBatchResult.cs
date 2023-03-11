// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

/// <summary>Exposes an enumerator that provides asynchronous iteration over values of a specified type.</summary>
/// <typeparam name="T">The type of values to enumerate.</typeparam>
public interface IJsonRpcBatchResult : IAsyncEnumerable<JsonRpcResult.Entry>
{
    /// <summary>Returns an enumerator that iterates asynchronously through the collection.</summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that may be used to cancel the asynchronous iteration.</param>
    /// <returns>An enumerator that can be used to iterate asynchronously through the collection.</returns>
    new JsonRpcBatchResultAsyncEnumerator GetAsyncEnumerator(CancellationToken cancellationToken = default);
}

public class JsonRpcBatchResultAsyncEnumerator : IAsyncEnumerator<JsonRpcResult.Entry>
{
    private readonly IAsyncEnumerator<JsonRpcResult.Entry> _enumerator;

    public JsonRpcBatchResultAsyncEnumerator(
        Func<JsonRpcBatchResultAsyncEnumerator, CancellationToken, IAsyncEnumerator<JsonRpcResult.Entry>> innerEnumeratorFactory,
        CancellationToken cancellationToken = default)
    {
        _enumerator = innerEnumeratorFactory(this, cancellationToken);
    }

    public bool IsStopped { get; set; }

    public ValueTask DisposeAsync() => _enumerator.DisposeAsync();

    public ValueTask<bool> MoveNextAsync() => _enumerator.MoveNextAsync();

    public JsonRpcResult.Entry Current => _enumerator.Current;
}
