// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
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

    /// <summary>Formats a raw integer <paramref name="amount"/> with <paramref name="decimals"/> as a decimal string without trailing zeros, such as <c>1.5</c>.</summary>
    public static string FormatUnits(in UInt256 amount, int decimals) => McpTokenMetadata.FormatUnits(amount, decimals);

    /// <summary>Formats a wei amount as gwei.</summary>
    public static string Gwei(in UInt256 wei) => FormatUnits(wei, GweiDecimals);

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

    /// <summary>Makes sure every tool of <paramref name="toolSet"/> advertises the schema from its <see cref="McpToolOutputSchemaAttribute"/>.</summary>
    /// <remarks>
    /// The SDK ignores <see cref="McpServerToolCreateOptions.OutputSchema"/> unless
    /// <see cref="McpServerToolCreateOptions.UseStructuredContent"/> is also set, so a schema missing from the created
    /// descriptor is copied onto it here. Tools that already carry a schema are left unchanged.
    /// </remarks>
    public static IEnumerable<McpServerTool> WithDeclaredOutputSchemas(IEnumerable<McpServerTool> tools, Type toolSet)
    {
        Dictionary<string, string> schemas = new(StringComparer.Ordinal);
        foreach (MethodInfo method in toolSet.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>()?.Name is { } name && method.GetCustomAttribute<McpToolOutputSchemaAttribute>() is { } schema)
            {
                schemas[name] = schema.Json;
            }
        }

        foreach (McpServerTool tool in tools)
        {
            if (tool.ProtocolTool.OutputSchema is null && schemas.TryGetValue(tool.ProtocolTool.Name, out string? json))
            {
                using JsonDocument document = JsonDocument.Parse(json);
                tool.ProtocolTool.OutputSchema = document.RootElement.Clone();
            }

            yield return tool;
        }
    }

    /// <summary>Returns a completed <c>invalid_input</c> error.</summary>
    public static Task<CallToolResult> InvalidInput(string message) =>
        Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, message));

    /// <summary>
    /// Adds a decoded <c>reason</c> (<c>{"kind", "message", "selector"}</c>) to an <c>execution_reverted</c> error and
    /// appends the reason to its message; other results are returned unchanged.
    /// </summary>
    public static CallToolResult WithRevertReason(CallToolResult result)
    {
        if (result.IsError != true
            || result.StructuredContent is not { } structured
            || !structured.TryGetProperty("error", out JsonElement error)
            || error.GetProperty("code").GetString() != McpToolErrorCodes.ExecutionReverted)
        {
            return result;
        }

        string message = error.GetProperty("message").GetString() ?? McpToolErrorCodes.ExecutionReverted;
        string? data = error.TryGetProperty("data", out JsonElement dataElement) ? dataElement.GetString() : null;
        byte[] revertData = data is { Length: > 2 } ? Bytes.FromHexString(data) : [];
        McpDecodedRevert reason = McpKnownAbi.DecodeRevert(revertData);

        if (!message.Contains(reason.Message, StringComparison.Ordinal))
        {
            message = $"{message} (reason: {reason.Message})";
        }

        ArrayBufferWriter<byte> buffer = new(256);
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Encoder = EthereumJsonSerializer.JsonOptions.Encoder }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("error"u8);
            writer.WriteStartObject();
            writer.WriteString("code"u8, McpToolErrorCodes.ExecutionReverted);
            writer.WriteString("message"u8, message);
            if (data is not null)
            {
                writer.WriteString("data"u8, data);
            }

            writer.WritePropertyName("reason"u8);
            writer.WriteStartObject();
            writer.WriteString("kind"u8, reason.Kind);
            writer.WriteString("message"u8, reason.Message);
            writer.WriteString("selector"u8, reason.Selector);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = document.RootElement.Clone(),
            Content = [new TextContentBlock { Text = Encoding.UTF8.GetString(buffer.WrittenSpan) }]
        };
    }
}
