// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Machine-readable codes placed in <c>error.code</c> of a failed MCP tool result.</summary>
internal static class McpToolErrorCodes
{
    /// <summary>An argument is missing, malformed or outside the allowed range.</summary>
    public const string InvalidInput = "invalid_input";

    /// <summary>The requested block, transaction or receipt does not exist on this node.</summary>
    public const string NotFound = "not_found";

    /// <summary>The call reverted; <c>error.data</c> carries the revert data as hex, when present.</summary>
    public const string ExecutionReverted = "execution_reverted";

    /// <summary>A size, count or concurrency limit was hit; narrowing the request or retrying later may succeed.</summary>
    public const string ResourceExhausted = "resource_exhausted";

    /// <summary>The tool did not finish within <see cref="IMcpConfig.ToolTimeout"/>.</summary>
    public const string Timeout = "timeout";

    /// <summary>The data exists in principle but this node cannot serve it (not synced, pruned, disabled).</summary>
    public const string Unavailable = "unavailable";

    /// <summary>An unexpected failure; details are only written to the node log.</summary>
    public const string InternalError = "internal_error";
}
