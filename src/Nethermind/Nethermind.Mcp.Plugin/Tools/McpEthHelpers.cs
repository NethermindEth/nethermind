// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Mcp.Plugin.Tools.Abi;
using Nethermind.Serialization.Json;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Shared helpers of the eth tool sets: block resolution, human-friendly formatting and error shaping.</summary>
internal static class McpEthHelpers
{
    /// <summary>The number of decimals of the native currency (ETH and xDAI).</summary>
    public const int NativeDecimals = 18;

    /// <summary>The number of decimals of a gwei amount expressed in wei.</summary>
    public const int GweiDecimals = 9;

    private const long MaxIsoTimestamp = 253402300799; // 9999-12-31T23:59:59Z

    /// <summary>Formats a wei amount in the native currency (ETH, or xDAI on Gnosis), such as <c>1.5</c>.</summary>
    public static string FormatNative(in UInt256 wei) => McpTokenMetadata.FormatUnits(wei, NativeDecimals);

    /// <summary>Formats a wei amount as gwei.</summary>
    public static string Gwei(in UInt256 wei) => McpTokenMetadata.FormatUnits(wei, GweiDecimals);

    /// <summary>Formats a Unix timestamp in seconds as ISO 8601 UTC, or returns <see langword="null"/> if it is out of range.</summary>
    public static string? ToIso(ulong unixSeconds) => unixSeconds > MaxIsoTimestamp
        ? null
        : DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Formats an address with its EIP-55 checksum.</summary>
    public static string Checksum(Address address) => address.ToString(withZeroX: true, withEip55Checksum: true);

    /// <summary>Returns <c>a + b</c>, saturating at <see cref="ulong.MaxValue"/>.</summary>
    public static ulong SaturatingAdd(ulong a, ulong b) => ulong.MaxValue - a < b ? ulong.MaxValue : a + b;

    /// <summary>Resolves a block selector to a block number, so availability checks and queries see the same block.</summary>
    /// <param name="blockFinder">The block finder.</param>
    /// <param name="block">The parsed selector.</param>
    /// <param name="parameter">The argument name, used in error messages.</param>
    /// <param name="requireCanonical">Whether a block hash must name a canonical block.</param>
    /// <param name="number">The resolved number.</param>
    /// <param name="failure">An error result when the block cannot be resolved.</param>
    public static bool TryResolveBlockNumber(
        IBlockFinder blockFinder,
        BlockParameter block,
        string parameter,
        bool requireCanonical,
        out ulong number,
        [NotNullWhen(false)] out CallToolResult? failure)
    {
        failure = null;
        if (block.BlockNumber is { } explicitNumber)
        {
            number = explicitNumber;
            return true;
        }

        number = 0;
        if (blockFinder.Head is null)
        {
            failure = McpToolExecutor.Error(McpToolErrorCodes.Unavailable, "The node has no head block yet; it is probably still starting or syncing.");
            return false;
        }

        BlockParameter lookup = requireCanonical && block.BlockHash is { } hash ? new BlockParameter(hash, requireCanonical: true) : block;
        SearchResult<BlockHeader> header = blockFinder.SearchForHeader(lookup);
        if (!header.IsError)
        {
            number = header.Object!.Number;
            return true;
        }

        failure = block.Type is BlockParameterType.Safe or BlockParameterType.Finalized
            ? McpToolExecutor.Error(McpToolErrorCodes.Unavailable, $"'{parameter}': the {block} block is not known to the node yet; use \"latest\" or a block number.")
            : header.ErrorCode == ErrorCodes.InvalidInput && header.Error != BlockFinderExtensions.HeaderNotFound
                ? McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, $"'{parameter}': {header.Error}")
                : McpToolExecutor.Error(McpToolErrorCodes.NotFound, $"'{parameter}': block not found.");
        return false;
    }

    /// <summary>
    /// Returns the error for a block <c>eth_getBlockBy*</c> did not return: <c>unavailable</c> when its header is known but its body
    /// is not stored (history expiry, fast sync without old bodies), otherwise <c>not_found</c> with <paramref name="notFoundMessage"/>.
    /// </summary>
    public static CallToolResult MissingBlock(IEthRpcModule eth, BlockParameter block, McpNodeCapabilities capabilities, string notFoundMessage)
    {
        using ResultWrapper<BlockHeaderForRpc?> header = block.BlockHash is { } hash ? eth.eth_getHeaderByHash(hash) : eth.eth_getHeaderByNumber(block);
        if (header.Result.ResultType == ResultType.Success && header.Data?.Number is { } number)
        {
            return capabilities.CheckBody((long)number)
                ?? McpToolExecutor.Error(McpToolErrorCodes.Unavailable, $"The header of block {number} is known but its body is not stored on this node.");
        }

        return McpToolExecutor.Error(McpToolErrorCodes.NotFound, notFoundMessage);
    }

    /// <summary>Returns a completed <c>invalid_input</c> error.</summary>
    public static Task<CallToolResult> InvalidInput(string message) =>
        Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, message));

    /// <summary>
    /// Adds a decoded <c>reason</c> (<c>{"kind", "message", "selector"}</c>) to an <c>execution_reverted</c> error and
    /// appends the reason to its message; other results are returned unchanged.
    /// </summary>
    /// <param name="result">The mapped failure of <paramref name="wrapper"/>.</param>
    /// <param name="wrapper">The failed JSON-RPC result, whose full revert data is decoded (the mapped error may carry it cut).</param>
    public static CallToolResult WithRevertReason(CallToolResult result, IResultWrapper wrapper)
    {
        if (McpToolExecutor.ReadError(result) is not { } error || error.GetProperty("code").GetString() != McpToolErrorCodes.ExecutionReverted)
        {
            return result;
        }

        string message = error.GetProperty("message").GetString() ?? McpToolErrorCodes.ExecutionReverted;
        string? data = wrapper.HasErrorData ? wrapper.Data as string : null;
        McpDecodedRevert reason = McpKnownAbi.DecodeRevert(FromHexOrEmpty(data));

        if (!message.Contains(reason.Message, StringComparison.Ordinal))
        {
            message = $"{message} (reason: {reason.Message})";
        }

        return RevertError(message, data, reason);
    }

    /// <summary>Decodes 0x-prefixed hex revert data, or returns no bytes when it is missing or malformed.</summary>
    public static byte[] FromHexOrEmpty(string? hex)
    {
        if (hex is not { Length: > 2 } || !hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || hex.Length % 2 != 0)
        {
            return [];
        }

        byte[] bytes = new byte[(hex.Length - 2) / 2];
        return Convert.FromHexString(hex.AsSpan(2), bytes, out _, out _) == OperationStatus.Done ? bytes : [];
    }

    /// <summary>Builds an <c>execution_reverted</c> error carrying the raw revert <paramref name="data"/> and the decoded <paramref name="reason"/>.</summary>
    public static CallToolResult RevertError(string message, string? data, McpDecodedRevert reason)
    {
        ArrayBufferWriter<byte> buffer = new(256);
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Encoder = EthereumJsonSerializer.JsonOptions.Encoder }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("error"u8);
            writer.WriteStartObject();
            writer.WriteString("code"u8, McpToolErrorCodes.ExecutionReverted);
            writer.WriteString("message"u8, message);
            McpToolExecutor.WriteErrorData(writer, data);
            writer.WritePropertyName("reason"u8);
            writer.WriteStartObject();
            writer.WriteString("kind"u8, reason.Kind);
            writer.WriteString("message"u8, reason.Message);
            writer.WriteString("selector"u8, reason.Selector);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = Encoding.UTF8.GetString(buffer.WrittenSpan) }] };
    }
}
