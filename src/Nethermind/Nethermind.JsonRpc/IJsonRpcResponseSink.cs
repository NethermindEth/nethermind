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
    ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken);

    /// <summary>Starts a JSON-RPC batch response.</summary>
    ValueTask BeginBatchAsync(CancellationToken cancellationToken);

    /// <summary>Writes one item in the current JSON-RPC batch response.</summary>
    ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken);

    /// <summary>Ends the current JSON-RPC batch response.</summary>
    ValueTask EndBatchAsync(CancellationToken cancellationToken);
}
