// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules.Subscribe;

internal static class SubscriptionArgs
{
    internal const int MaxArgsStringLength = 1_000_000;

    /// <summary>
    /// Returns an <c>Invalid params</c> failure when <paramref name="args"/> exceeds the maximum
    /// allowed length, or <c>null</c> when it is within bounds.
    /// </summary>
    /// <remarks>
    /// Bounds peak memory on the subscribe path before any JSON re-parsing. Used at the RPC
    /// entrypoint so the failure can be surfaced as a normal result rather than via an exception.
    /// </remarks>
    internal static ResultWrapper<string>? CheckArgsLength(string? args) =>
        args is { Length: > MaxArgsStringLength }
            ? ResultWrapper<string>.Fail("Invalid params", ErrorCodes.InvalidParams,
                $"subscription args string length {args.Length} exceeds maximum allowed length of {MaxArgsStringLength}")
            : null;
}
