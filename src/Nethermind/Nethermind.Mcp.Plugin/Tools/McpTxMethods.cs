// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Frozen;
using Nethermind.Core.Crypto;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>A small built-in table of well-known function selectors, used to name the method a transaction calls.</summary>
/// <remarks>
/// Selectors are derived from the canonical signatures at startup, so the table cannot drift from the signatures. It is
/// deliberately small and offline: an unknown selector is reported as such rather than looked up in an external service.
/// </remarks>
internal static class McpTxMethods
{
    private static readonly string[] Signatures =
    [
        // ERC-20 / ERC-721 / ERC-1155 and permits
        "transfer(address,uint256)",
        "approve(address,uint256)",
        "transferFrom(address,address,uint256)",
        "increaseAllowance(address,uint256)",
        "decreaseAllowance(address,uint256)",
        "permit(address,address,uint256,uint256,uint8,bytes32,bytes32)",
        "safeTransferFrom(address,address,uint256)",
        "safeTransferFrom(address,address,uint256,bytes)",
        "safeTransferFrom(address,address,uint256,uint256,bytes)",
        "safeBatchTransferFrom(address,address,uint256[],uint256[],bytes)",
        "setApprovalForAll(address,bool)",
        "mint(address,uint256)",
        "burn(uint256)",
        // WETH and ERC-4626 vaults
        "deposit()",
        "withdraw(uint256)",
        "deposit(uint256)",
        "deposit(uint256,address)",
        "mint(uint256,address)",
        "withdraw(uint256,address,address)",
        "redeem(uint256,address,address)",
        // Batching
        "multicall(bytes[])",
        "multicall(uint256,bytes[])",
        "multicall(bytes32,bytes[])",
        "aggregate((address,bytes)[])",
        "aggregate3((address,bool,bytes)[])",
        "aggregate3Value((address,bool,uint256,bytes)[])",
        "tryAggregate(bool,(address,bytes)[])",
        // Uniswap V2 style routers and pairs
        "swapExactTokensForTokens(uint256,uint256,address[],address,uint256)",
        "swapTokensForExactTokens(uint256,uint256,address[],address,uint256)",
        "swapExactETHForTokens(uint256,address[],address,uint256)",
        "swapETHForExactTokens(uint256,address[],address,uint256)",
        "swapExactTokensForETH(uint256,uint256,address[],address,uint256)",
        "swapTokensForExactETH(uint256,uint256,address[],address,uint256)",
        "swapExactTokensForTokensSupportingFeeOnTransferTokens(uint256,uint256,address[],address,uint256)",
        "swapExactETHForTokensSupportingFeeOnTransferTokens(uint256,address[],address,uint256)",
        "swapExactTokensForETHSupportingFeeOnTransferTokens(uint256,uint256,address[],address,uint256)",
        "addLiquidity(address,address,uint256,uint256,uint256,uint256,address,uint256)",
        "addLiquidityETH(address,uint256,uint256,uint256,address,uint256)",
        "removeLiquidity(address,address,uint256,uint256,uint256,address,uint256)",
        "removeLiquidityETH(address,uint256,uint256,uint256,address,uint256)",
        "swap(uint256,uint256,address,bytes)",
        // Uniswap V3 routers and pools
        "exactInputSingle((address,address,uint24,address,uint256,uint256,uint256,uint160))",
        "exactInput((bytes,address,uint256,uint256,uint256))",
        "exactOutputSingle((address,address,uint24,address,uint256,uint256,uint256,uint160))",
        "exactOutput((bytes,address,uint256,uint256,uint256))",
        "exactInputSingle((address,address,uint24,address,uint256,uint256,uint160))",
        "exactInput((bytes,address,uint256,uint256))",
        "exactOutputSingle((address,address,uint24,address,uint256,uint256,uint160))",
        "exactOutput((bytes,address,uint256,uint256))",
        "swap(address,bool,int256,uint160,bytes)",
        // Uniswap universal router, account abstraction and smart accounts
        "execute(bytes,bytes[],uint256)",
        "execute(bytes,bytes[])",
        "execute(address,uint256,bytes)",
        "executeBatch(address[],uint256[],bytes[])",
        "executeBatch(address[],bytes[])",
        "execTransaction(address,uint256,bytes,uint8,uint256,uint256,uint256,address,address,bytes)",
        "handleOps((address,uint256,bytes,bytes,uint256,uint256,uint256,uint256,uint256,bytes,bytes)[],address)",
        "handleOps((address,uint256,bytes,bytes,bytes32,uint256,bytes32,bytes,bytes)[],address)",
        // Staking, bridges and claims
        "deposit(bytes,bytes,bytes,bytes32)",
        "deposit(bytes,bytes,bytes,bytes32,uint256)",
        "claimWithdrawal(address)",
        "claimWithdrawals(address[])",
        "relayTokens(address)",
        "relayTokens(address,uint256)",
        "relayTokens(address,address,uint256)",
        "claim(uint256,address,uint256,bytes32[])",
        "register(string,address,uint256,bytes32,address,bytes[],bool,uint16)",
        "setName(string)",
    ];

    private static readonly FrozenDictionary<uint, string> BySelector = Build();

    /// <summary>Returns the canonical signature of a well-known selector, or <see langword="null"/> if it is not in the table.</summary>
    /// <param name="input">Call data; only its first four bytes are used.</param>
    public static string? TryName(ReadOnlySpan<byte> input) =>
        input.Length >= 4 && BySelector.TryGetValue(BinaryPrimitives.ReadUInt32BigEndian(input), out string? name) ? name : null;

    /// <summary>Returns the name part of a signature, such as <c>transfer</c> for <c>transfer(address,uint256)</c>.</summary>
    public static string ShortName(string signature)
    {
        int paren = signature.IndexOf('(');
        return paren < 0 ? signature : signature[..paren];
    }

    private static FrozenDictionary<uint, string> Build()
    {
        Dictionary<uint, string> table = new(Signatures.Length);
        foreach (string signature in Signatures)
        {
            ValueHash256 hash = ValueKeccak.Compute(signature);
            // Keep the first signature on the rare selector collision; the table order lists the more common one first.
            table.TryAdd(BinaryPrimitives.ReadUInt32BigEndian(hash.Bytes), signature);
        }

        return table.ToFrozenDictionary();
    }
}
