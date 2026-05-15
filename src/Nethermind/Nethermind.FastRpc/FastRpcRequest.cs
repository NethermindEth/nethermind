// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;

namespace Nethermind.FastRpc;

/// <summary>
/// Request metadata passed to a registered fast RPC handler.
/// </summary>
/// <param name="method">The REST route name or JSON-RPC method.</param>
/// <param name="hasParams">Whether the JSON-RPC request supplied params.</param>
/// <param name="paramsElement">The raw JSON params element for JSON-RPC requests.</param>
/// <param name="body">The raw REST POST body.</param>
public readonly struct FastRpcRequest(string method, bool hasParams, JsonElement paramsElement, ReadOnlyMemory<byte> body = default)
{
    /// <summary>
    /// REST route name or JSON-RPC method.
    /// </summary>
    public string Method { get; } = method;

    /// <summary>
    /// Whether <see cref="Params"/> contains a request params value.
    /// </summary>
    public bool HasParams { get; } = hasParams;

    /// <summary>
    /// Raw JSON-RPC params. Undefined for REST requests and notifications without params.
    /// </summary>
    public JsonElement Params { get; } = paramsElement;

    /// <summary>
    /// Raw REST POST body. Empty for GET and JSON-RPC requests.
    /// </summary>
    public ReadOnlyMemory<byte> Body { get; } = body;
}
