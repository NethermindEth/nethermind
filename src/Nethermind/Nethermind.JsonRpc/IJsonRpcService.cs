// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

public interface IJsonRpcService
{
    /// <param name="cancellationToken">Cancelled when the request is aborted, such as by its client disconnecting.</param>
    /// <remarks>
    /// A request the token abandons, while queued for a module or while its method stops on the cancellation, throws an
    /// <see cref="System.OperationCanceledException"/> for this token instead of returning a response.
    /// </remarks>
    ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context, CancellationToken cancellationToken = default);
    JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, in JsonRpcId id, string? methodName = null);
    JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, string? methodName = null);
}
