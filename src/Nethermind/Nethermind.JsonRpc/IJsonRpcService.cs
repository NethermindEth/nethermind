// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

public interface IJsonRpcService
{
    /// <summary>Processes one JSON-RPC request.</summary>
    ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context);

    /// <summary>Processes one JSON-RPC request with an optional cancellation-aware implementation.</summary>
    /// <remarks>
    /// The default compatibility implementation forwards to the legacy two-argument member and therefore ignores the
    /// cancellation token. Implementations that observe cancellation should override this member.
    /// </remarks>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the request produced a response; none is produced then.
    /// </exception>
    ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context, CancellationToken cancellationToken) =>
        SendRequestAsync(request, context);

    JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, in JsonRpcId id, string? methodName = null);
    JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, string? methodName = null);
}
