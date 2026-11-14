// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Nethermind.Tools.ProfilingMcp;

internal static class ToolErrors
{
    /// <summary>
    /// Returns a failing tool's exception message to the agent.
    /// </summary>
    /// <remarks>
    /// The SDK otherwise replaces it with a generic "An error occurred invoking" text, which leaves the agent unable to react,
    /// e.g. to a refused ptrace attach or an unsupported snapshot type.
    /// </remarks>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> ReportExceptionMessages(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                string message = e.InnerException is null || e.Message.Contains(e.InnerException.Message, StringComparison.Ordinal)
                    ? e.Message
                    : $"{e.Message} ({e.InnerException.Message})";
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"{e.GetType().Name}: {message}" }] };
            }
        };
}
