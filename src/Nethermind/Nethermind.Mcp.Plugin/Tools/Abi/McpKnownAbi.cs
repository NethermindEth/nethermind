// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Mcp.Plugin.Tools.Abi;

/// <summary>A decoded ABI value: <c>Value</c> is JSON-friendly (checksummed address string, decimal string for integers, 0x hex for bytes, bool, string, or a list for arrays/tuples).</summary>
public sealed record McpDecodedParam(string Name, string Type, bool Indexed, object? Value);

/// <summary>A log decoded against a known event.</summary>
/// <param name="Event">The event name, such as <c>Transfer</c>.</param>
/// <param name="Signature">The canonical signature, such as <c>Transfer(address,address,uint256)</c>.</param>
/// <param name="Standard">The token standard the event belongs to (<c>ERC-20</c>, <c>ERC-721</c>, <c>ERC-1155</c>, <c>WETH</c>), if any.</param>
/// <param name="Params">The decoded parameters in declaration order.</param>
public sealed record McpDecodedLog(string Event, string Signature, string? Standard, IReadOnlyList<McpDecodedParam> Params);

/// <summary>Decoded revert data.</summary>
/// <param name="Kind"><c>Error</c> for <c>Error(string)</c>, <c>Panic</c> for <c>Panic(uint256)</c>, <c>Custom</c> for other selectors, <c>Empty</c> for no data.</param>
/// <param name="Message">A human-readable reason: the string, the panic meaning (for example "arithmetic overflow or underflow") or the selector.</param>
/// <param name="Selector">The 4-byte selector as 0x hex, if any.</param>
public sealed record McpDecodedRevert(string Kind, string Message, string? Selector);

/// <summary>Decoding against well-known events and errors (ERC-20/721/1155 transfers and approvals, WETH deposits and withdrawals, Error/Panic).</summary>
/// <remarks>Stub: the SEMANTIC agent implements it.</remarks>
public static class McpKnownAbi
{
    /// <summary>Decodes <paramref name="log"/> against the known events, or returns <see langword="null"/> if it matches none.</summary>
    public static McpDecodedLog? TryDecodeLog(LogEntry log) => null;

    /// <summary>Decodes revert data (<c>Error(string)</c>, <c>Panic(uint256)</c>, custom selector or empty).</summary>
    public static McpDecodedRevert DecodeRevert(ReadOnlySpan<byte> data) => new("Empty", "reverted without data", null);
}
