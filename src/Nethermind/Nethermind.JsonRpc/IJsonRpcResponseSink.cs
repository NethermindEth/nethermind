// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

/// <summary>Receives JSON-RPC responses from the processor and writes them to a transport-specific output.</summary>
public interface IJsonRpcResponseSink
{
    /// <summary>Gets the number of response bytes written by the sink.</summary>
    long BytesWritten { get; }

    /// <summary>Gets whether batch processing should stop after the last written item.</summary>
    bool StopRequested { get; }

    /// <summary>Writes one non-batch JSON-RPC response.</summary>
    /// <param name="response">The response to write.</param>
    /// <param name="report">The RPC report associated with the response.</param>
    /// <param name="cancellationToken">The cancellation token for transport writes.</param>
    ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken);

    /// <summary>Starts a JSON-RPC batch response.</summary>
    /// <param name="cancellationToken">The cancellation token for transport writes.</param>
    ValueTask BeginBatchAsync(CancellationToken cancellationToken);

    /// <summary>Writes one item in the current JSON-RPC batch response.</summary>
    /// <param name="response">The response item to write.</param>
    /// <param name="report">The RPC report associated with the response item.</param>
    /// <param name="cancellationToken">The cancellation token for transport writes.</param>
    ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken);

    /// <summary>Ends the current JSON-RPC batch response.</summary>
    /// <param name="cancellationToken">The cancellation token for transport writes.</param>
    ValueTask EndBatchAsync(CancellationToken cancellationToken);
}
