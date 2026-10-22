// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc;

/// <summary>Receives the result of a streamed response as the node writes it, attached with <see cref="JsonRpcResponse.ObserveStreamedResult"/>.</summary>
/// <remarks>
/// Only the result value is reported, without the envelope or the id. Bytes are reported as they are written, which can
/// be before the transport has them, so a result the writer replaces with an error has been partly reported when
/// <see cref="OnCompleted"/> says it did not succeed. An exception the observer throws does not change the response: it is logged, the
/// observer gets no more chunks, and <see cref="OnCompleted"/> says the result did not succeed.
/// A response that is disposed without being written never calls <see cref="OnCompleted"/>, so release resources with
/// <see cref="JsonRpcResponse.AddDisposable"/>. A response has one observer at a time: a later
/// <see cref="JsonRpcResponse.ObserveStreamedResult"/> call replaces it.
/// </remarks>
public interface IStreamedResultObserver
{
    /// <summary>Called with each chunk of the result as it is written.</summary>
    /// <remarks>Runs synchronously on the thread that writes the response, so it must be fast and must not block.</remarks>
    /// <param name="utf8Json">The chunk, valid only during the call.</param>
    void OnWritten(ReadOnlySpan<byte> utf8Json);

    /// <summary>Called once, after the response is written or its writing failed.</summary>
    /// <param name="succeeded">Whether the whole result was written: <see langword="false"/> when it was replaced by an error,
    /// ended with a status other than complete, was cancelled, stopped because a flush reported that the reader completed or the
    /// flush was cancelled, or failed while it was written.</param>
    void OnCompleted(bool succeeded);
}
