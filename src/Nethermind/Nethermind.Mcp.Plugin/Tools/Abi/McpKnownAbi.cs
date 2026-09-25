// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Mcp.Plugin.Tools.Abi;

/// <summary>A decoded ABI value: <c>Value</c> is JSON-friendly (checksummed address string, decimal string for integers, 0x hex for bytes, bool, string, or a list for arrays/tuples).</summary>
public sealed record McpDecodedParam(string Name, string Type, bool Indexed, object? Value);

/// <summary>A log decoded against a known event.</summary>
/// <param name="Event">The event name, such as <c>Transfer</c>.</param>
/// <param name="Signature">The canonical signature, such as <c>Transfer(address,address,uint256)</c>.</param>
/// <param name="Standard">The token standard the event belongs to (<c>ERC-20</c>, <c>ERC-721</c>, <c>ERC-1155</c>, <c>WETH</c>), if any.</param>
/// <param name="Params">The decoded parameters in declaration order.</param>
public sealed record McpDecodedLog(string Event, string Signature, string? Standard, IReadOnlyList<McpDecodedParam> Params)
{
    /// <summary>Returns the value of the parameter named <paramref name="name"/>, or <see langword="null"/> if absent.</summary>
    public object? Get(string name)
    {
        foreach (McpDecodedParam param in Params)
        {
            if (param.Name == name) return param.Value;
        }

        return null;
    }
}

/// <summary>Decoded revert data.</summary>
/// <param name="Kind"><c>Error</c> for <c>Error(string)</c>, <c>Panic</c> for <c>Panic(uint256)</c>, <c>Custom</c> for other selectors, <c>Empty</c> for no data.</param>
/// <param name="Message">A human-readable reason: the string, the panic meaning (for example "arithmetic overflow or underflow") or the selector.</param>
/// <param name="Selector">The 4-byte selector as 0x hex, if any.</param>
public sealed record McpDecodedRevert(string Kind, string Message, string? Selector);

/// <summary>Decoding against well-known events and errors (ERC-20/721/1155 transfers and approvals, WETH deposits and withdrawals, Error/Panic).</summary>
/// <remarks>
/// Known events: ERC-20 <c>Transfer</c>/<c>Approval</c> (3 topics), ERC-721 <c>Transfer</c>/<c>Approval</c> (4 topics, tokenId indexed),
/// <c>ApprovalForAll</c>, ERC-1155 <c>TransferSingle</c>/<c>TransferBatch</c>/<c>URI</c>, WETH/WXDAI <c>Deposit</c>/<c>Withdrawal</c>,
/// Uniswap V2 <c>Swap</c>/<c>Sync</c>/<c>Mint</c>/<c>Burn</c>/<c>PairCreated</c>, Uniswap V3 <c>Swap</c>, Ownable <c>OwnershipTransferred</c>
/// and EIP-1967 <c>Upgraded</c>/<c>AdminChanged</c>/<c>BeaconUpgraded</c>. Every method is total: malformed input yields
/// <see langword="null"/> or a <c>Custom</c> revert, never an exception.
/// </remarks>
public static class McpKnownAbi
{
    /// <summary>The <c>Error(string)</c> selector.</summary>
    public const string ErrorSelector = "0x08c379a0";

    /// <summary>The <c>Panic(uint256)</c> selector.</summary>
    public const string PanicSelector = "0x4e487b71";

    /// <summary>The standard label of ERC-20 events.</summary>
    public const string Erc20 = "ERC-20";

    /// <summary>The standard label of ERC-721 events.</summary>
    public const string Erc721 = "ERC-721";

    /// <summary>The standard label of ERC-1155 events.</summary>
    public const string Erc1155 = "ERC-1155";

    /// <summary>The standard label of wrapped-native-token events (WETH on Ethereum, WXDAI on Gnosis).</summary>
    public const string Weth = "WETH";

    private const int MaxRevertMessageLength = 1024;

    private static readonly McpAbiParam[] StringParam = [new(string.Empty, McpAbiType.String)];
    private static readonly McpAbiParam[] UInt256Param = [new(string.Empty, McpAbiType.UInt256)];

    private static readonly Dictionary<Hash256, KnownEvent[]> Events = BuildEvents();

    /// <summary>Gets the ERC-20 <c>Transfer(address,address,uint256)</c> topic0, shared with ERC-721.</summary>
    public static Hash256 TransferTopic { get; } = Keccak.Compute("Transfer(address,address,uint256)");

    /// <summary>Decodes <paramref name="log"/> against the known events, or returns <see langword="null"/> if it matches none.</summary>
    /// <param name="log">The log to decode.</param>
    /// <param name="wrappedNativeToken">
    /// The chain's wrapped native token. <c>Deposit</c>/<c>Withdrawal</c> are generic names, so they get the <c>WETH</c> standard
    /// only when emitted by this contract; from any other emitter they decode as plain events without a standard.
    /// </param>
    public static McpDecodedLog? TryDecodeLog(LogEntry log, Address? wrappedNativeToken = null)
    {
        if (log?.Topics is not { Length: > 0 } topics || topics[0] is null || !Events.TryGetValue(topics[0], out KnownEvent[]? candidates))
        {
            return null;
        }

        foreach (KnownEvent candidate in candidates)
        {
            string? standard = candidate.Standard == Weth && log.Address != wrappedNativeToken ? null : candidate.Standard;
            if (McpAbiCodec.TryDecodeEvent(candidate.Signature, log, standard, out McpDecodedLog? decoded))
            {
                return decoded;
            }
        }

        return null;
    }

    /// <summary>Decodes revert data (<c>Error(string)</c>, <c>Panic(uint256)</c>, custom selector or empty).</summary>
    public static McpDecodedRevert DecodeRevert(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return new McpDecodedRevert("Empty", "reverted without a reason (no revert data)", null);
        }

        if (data.Length < 4)
        {
            return new McpDecodedRevert("Custom", $"reverted with non-standard data 0x{Convert.ToHexStringLower(data)}", null);
        }

        string selector = "0x" + Convert.ToHexStringLower(data[..4]);
        ReadOnlySpan<byte> payload = data[4..];
        switch (selector)
        {
            case ErrorSelector:
                if (McpAbiCodec.TryDecode(StringParam, payload, out object?[]? message, out _) && message[0] is string text)
                {
                    return new McpDecodedRevert("Error", SanitizeRevertText(text), selector);
                }

                return new McpDecodedRevert("Custom", "reverted with a malformed Error(string) payload", selector);
            case PanicSelector:
                if (payload.Length == 32 && McpAbiCodec.TryDecode(UInt256Param, payload, out object?[]? code, out _) && code[0] is string codeText)
                {
                    BigInteger value = BigInteger.Parse(codeText, CultureInfo.InvariantCulture);
                    string hex = value <= 0xff ? ((int)value).ToString("x2", CultureInfo.InvariantCulture) : value.ToString("x", CultureInfo.InvariantCulture).TrimStart('0');
                    return new McpDecodedRevert("Panic", $"panic 0x{hex}: {DescribePanic(value)}", selector);
                }

                return new McpDecodedRevert("Custom", "reverted with a malformed Panic(uint256) payload", selector);
            default:
                return new McpDecodedRevert("Custom", $"reverted with custom error {selector} ({payload.Length} bytes of arguments); decode it with the contract's error ABI", selector);
        }
    }

    /// <summary>Describes a Solidity panic code, following the Solidity documentation.</summary>
    public static string DescribePanic(BigInteger code) => (int)(code > 0xff ? -1 : code) switch
    {
        0x00 => "generic compiler-inserted panic",
        0x01 => "assertion failed (assert)",
        0x11 => "arithmetic overflow or underflow",
        0x12 => "division or modulo by zero",
        0x21 => "invalid enum value",
        0x22 => "incorrectly encoded storage byte array",
        0x31 => "pop() on an empty array",
        0x32 => "array index out of bounds",
        0x41 => "out of memory (allocation too large)",
        0x51 => "call to an uninitialized internal function",
        _ => "unknown panic code"
    };

    /// <summary>Makes an untrusted revert string safe to show: control and format characters become spaces, and it is cut to a bounded length.</summary>
    public static string SanitizeRevertText(string text)
    {
        Span<char> buffer = stackalloc char[Math.Min(text.Length, MaxRevertMessageLength)];
        for (int i = 0; i < buffer.Length; i++)
        {
            char c = text[i];
            buffer[i] = McpTokenMetadata.IsUnsafeChar(c) ? ' ' : c;
        }

        string result = new(buffer);
        return text.Length > MaxRevertMessageLength ? result + "..." : result;
    }

    private static Dictionary<Hash256, KnownEvent[]> BuildEvents()
    {
        (string Signature, string? Standard)[] definitions =
        [
            ("event Transfer(address indexed from, address indexed to, uint256 value)", Erc20),
            ("event Transfer(address indexed from, address indexed to, uint256 indexed tokenId)", Erc721),
            ("event Approval(address indexed owner, address indexed spender, uint256 value)", Erc20),
            ("event Approval(address indexed owner, address indexed approved, uint256 indexed tokenId)", Erc721),
            ("event ApprovalForAll(address indexed owner, address indexed operator, bool approved)", "ERC-721/ERC-1155"),
            ("event TransferSingle(address indexed operator, address indexed from, address indexed to, uint256 id, uint256 value)", Erc1155),
            ("event TransferBatch(address indexed operator, address indexed from, address indexed to, uint256[] ids, uint256[] values)", Erc1155),
            ("event URI(string value, uint256 indexed id)", Erc1155),
            ("event Deposit(address indexed dst, uint256 wad)", Weth),
            ("event Withdrawal(address indexed src, uint256 wad)", Weth),
            ("event Swap(address indexed sender, uint256 amount0In, uint256 amount1In, uint256 amount0Out, uint256 amount1Out, address indexed to)", "Uniswap V2"),
            ("event Sync(uint112 reserve0, uint112 reserve1)", "Uniswap V2"),
            ("event Mint(address indexed sender, uint256 amount0, uint256 amount1)", "Uniswap V2"),
            ("event Burn(address indexed sender, uint256 amount0, uint256 amount1, address indexed to)", "Uniswap V2"),
            ("event PairCreated(address indexed token0, address indexed token1, address pair, uint256 index)", "Uniswap V2"),
            ("event Swap(address indexed sender, address indexed recipient, int256 amount0, int256 amount1, uint160 sqrtPriceX96, uint128 liquidity, int24 tick)", "Uniswap V3"),
            ("event OwnershipTransferred(address indexed previousOwner, address indexed newOwner)", "Ownable"),
            ("event Upgraded(address indexed implementation)", "EIP-1967"),
            ("event AdminChanged(address previousAdmin, address newAdmin)", "EIP-1967"),
            ("event BeaconUpgraded(address indexed beacon)", "EIP-1967"),
        ];

        Dictionary<Hash256, List<KnownEvent>> byTopic = [];
        foreach ((string text, string? standard) in definitions)
        {
            McpAbiSignature signature = McpAbiSignature.Parse(text, McpAbiSignatureKind.Event);
            if (!byTopic.TryGetValue(signature.Hash, out List<KnownEvent>? list))
            {
                byTopic[signature.Hash] = list = [];
            }

            list.Add(new KnownEvent(signature, standard));
        }

        Dictionary<Hash256, KnownEvent[]> result = new(byTopic.Count);
        foreach (KeyValuePair<Hash256, List<KnownEvent>> pair in byTopic) result[pair.Key] = [.. pair.Value];
        return result;
    }

    private sealed record KnownEvent(McpAbiSignature Signature, string? Standard);
}
