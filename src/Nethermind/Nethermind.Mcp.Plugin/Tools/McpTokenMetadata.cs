// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Token metadata read from the contract; any field may be missing for non-standard tokens.</summary>
public sealed record McpTokenInfo(Address Address, string? Name, string? Symbol, byte? Decimals);

/// <summary>Reads and caches ERC-20/721 metadata (<c>name</c>, <c>symbol</c>, <c>decimals</c>) via <c>eth_call</c>.</summary>
/// <remarks>Stub: the SEMANTIC agent implements it. Callers pass an eth module they already rented.</remarks>
public sealed class McpTokenMetadata
{
    /// <summary>Returns the token metadata at <paramref name="block"/>, or <see langword="null"/> if <paramref name="token"/> has no code.</summary>
    public McpTokenInfo? Get(IEthRpcModule eth, Address token, BlockParameter block) => null;

    /// <summary>Formats a raw integer amount with <paramref name="decimals"/> as a decimal string, such as <c>1.5</c>.</summary>
    public static string FormatUnits(Nethermind.Int256.UInt256 amount, int decimals) => amount.ToString();
}
