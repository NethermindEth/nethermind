// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.JsonRpc;

/// <summary>Thrown while decoding a well-formed JSON value that is not a valid JSON-RPC 2.0 Request object.</summary>
/// <remarks>
/// Derives from <see cref="JsonException"/> so every decode guard still catches it; the single-request paths catch it
/// first to answer -32600 Invalid Request rather than -32700 Parse error (JSON-RPC 2.0 section 5.1).
/// </remarks>
internal sealed class JsonRpcInvalidRequestException(string message) : JsonException(message);
