// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.FastRpc;

/// <summary>
/// Configures the standalone low-allocation RPC transport.
/// </summary>
public sealed class FastRpcOptions
{
    /// <summary>
    /// HTTP path prefix for REST endpoints. The default treats any non-root sub URL as REST.
    /// </summary>
    public string RestPathPrefix { get; init; } = "/";

    /// <summary>
    /// HTTP path for JSON-RPC POST requests.
    /// </summary>
    public string JsonRpcPath { get; init; } = "/";

    /// <summary>
    /// HTTP path for JSON-RPC websocket requests.
    /// </summary>
    public string WebSocketPath { get; init; } = "/ws";

    /// <summary>
    /// Optional HS256 JWT secret. When set, every route requires a valid bearer token.
    /// </summary>
    public byte[]? JwtSecret { get; init; }

    /// <summary>
    /// Buffers REST POST bodies into <see cref="FastRpcRequest.Body"/>. Disabled by default so large
    /// POST bodies can be drained without creating large object heap allocations on hot routes.
    /// </summary>
    public bool BufferRestRequestBody { get; init; }

    /// <summary>
    /// Whether bearer JWT authentication is enabled.
    /// </summary>
    public bool RequireJwt => JwtSecret is not null;
}
