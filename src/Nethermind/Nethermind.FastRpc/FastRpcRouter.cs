// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.FastRpc;

/// <summary>
/// Handles a REST or JSON-RPC request and returns pre-encoded transport bodies.
/// </summary>
public delegate ValueTask<FastRpcResponse> FastRpcHandler(FastRpcRequest request, CancellationToken cancellationToken);

/// <summary>
/// Mutable route builder for the standalone RPC server.
/// </summary>
public sealed class FastRpcRouter
{
    private readonly Dictionary<string, FastRpcHandler> _handlers = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a route name and JSON-RPC method.
    /// </summary>
    public FastRpcRouter Map(string method, FastRpcHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[method] = handler;
        return this;
    }

    /// <summary>
    /// Builds an immutable application request delegate.
    /// </summary>
    public FastRpcApplication Build(FastRpcOptions? options = null) =>
        new(_handlers, options ?? new FastRpcOptions());
}
