// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using ModelContextProtocol.Protocol;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>What this node can serve right now: head, sync state and the oldest block with state, bodies and receipts.</summary>
/// <param name="HeadNumber">The current head block number.</param>
/// <param name="IsSyncing">Whether the node is still catching up with the network.</param>
/// <param name="OldestStateBlock">The oldest block whose state can be queried, or <see langword="null"/> if none (state not synced).</param>
/// <param name="OldestBodyBlock">The oldest block whose body (transactions) is stored, or <see langword="null"/> if unknown.</param>
/// <param name="OldestReceiptBlock">The oldest block whose receipts are stored, or <see langword="null"/> if unknown.</param>
public sealed record McpDataAvailability(long HeadNumber, bool IsSyncing, long? OldestStateBlock, long? OldestBodyBlock, long? OldestReceiptBlock);

/// <summary>Reports which blocks this node can serve so tools can fail with errors that name the available range.</summary>
/// <remarks>Stub: the NODE agent implements it. Every Check* method returns <see langword="null"/> when the data is available.</remarks>
public sealed class McpNodeCapabilities
{
    /// <summary>Gets the current data availability.</summary>
    public McpDataAvailability GetAvailability() => new(0, false, 0, 0, 0);

    /// <summary>Returns an <c>unavailable</c> error naming the available state range, or <see langword="null"/> if state at <paramref name="blockNumber"/> is available.</summary>
    public CallToolResult? CheckState(long blockNumber) => null;

    /// <summary>Returns an <c>unavailable</c> error naming the available body range, or <see langword="null"/> if the body of <paramref name="blockNumber"/> is available.</summary>
    public CallToolResult? CheckBody(long blockNumber) => null;

    /// <summary>Returns an <c>unavailable</c> error naming the available receipt range, or <see langword="null"/> if receipts of <paramref name="blockNumber"/> are available.</summary>
    public CallToolResult? CheckReceipts(long blockNumber) => null;
}
