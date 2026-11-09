// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

public interface IJsonRpcService
{
    ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context);

    /// <summary>Handles <paramref name="request"/>; cancelling <paramref name="cancellationToken"/> abandons a module rental that is still queued.</summary>
    /// <remarks>
    /// The token is the transport's abort signal. An abandoned rental throws an <see cref="System.OperationCanceledException"/>
    /// for this token instead of returning a response; a rental already granted and the execution it starts are not cancelled.
    /// The default implementation ignores the token, so an implementation that does not override it keeps its old behaviour.
    /// </remarks>
    ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context, CancellationToken cancellationToken) =>
        SendRequestAsync(request, context);

    JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, in JsonRpcId id, string? methodName = null);
    JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, string? methodName = null);
}
