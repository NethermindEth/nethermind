// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc;

public static class JsonRpcLimits
{
    /// <summary>
    /// Maximum length, in characters, of a JSON-encoded string argument that is later
    /// re-parsed via <see cref="System.Text.Json.JsonDocument.Parse(string, System.Text.Json.JsonDocumentOptions)"/>.
    /// </summary>
    /// <remarks>
    /// Bounds peak memory when callers pass JSON inside a string parameter — e.g. the
    /// <c>args</c> string of <c>eth_subscribe</c>/<c>admin_subscribe</c>, or a stringified
    /// filter object passed to <c>eth_getLogs</c>.
    /// </remarks>
    public const int MaxJsonStringArgLength = 1_000_000;
}
