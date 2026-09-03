// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

public interface IJsonRpcService
{
    /// <summary>Processes one JSON-RPC request.</summary>
    ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context);

    /// <summary>Processes one JSON-RPC request, observing cancellation from the owning connection.</summary>
    /// <remarks>
    /// The default implementation preserves source and binary compatibility for out-of-tree service implementations
    /// that predate connection cancellation support.
    /// </remarks>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the request produced a response; none is produced then.
    /// </exception>
    ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context, CancellationToken cancellationToken) =>
        SendRequestAsync(request, context);

    JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, in JsonRpcId id, string? methodName = null);
    JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, string? methodName = null);
}
