// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;

namespace Nethermind.Arkiv.Rpc;

/// <summary>
/// <c>arkiv_*</c>. Reads at the head are Arkiv's own (<c>arkiv-reth-rpc</c> over the GolemDB store, natively), so their
/// answers and errors are the reference node's. GolemDB keeps no history, so a read at a past block refuses there with
/// <c>-32006</c>; this node also archives every entity after every block, and answers <c>arkiv_getEntity</c> at any
/// past block from that archive.
/// </summary>
public sealed class ArkivRpcModule(ArkivChain chain, IBlockTree blockTree) : IArkivRpcModule
{
    public const string ModuleName = "arkiv";

    public ResultWrapper<JsonElement?> arkiv_getEntity(Hash256 key, string? block = null)
    {
        if (!TryBlock(block, out ulong? requested)) return InvalidBlock(block);
        if (requested is { } number && number < chain.Head.Number)
        {
            (bool found, JsonElement? entity) = chain.EntityAt(key, number);
            if (!found || entity is null) return ResultWrapper<JsonElement?>.Success(null);
            return ResultWrapper<JsonElement?>.Success(IsLive(entity.Value, number) ? entity : null);
        }
        return Native("arkiv_getEntity", Params(key.ToString(), requested));
    }

    public ResultWrapper<JsonElement?> arkiv_debugEntityExists(Hash256 key, string? block = null)
    {
        if (!TryBlock(block, out ulong? requested)) return InvalidBlock(block);
        if (requested is { } number && number < chain.Head.Number)
        {
            (bool found, JsonElement? entity) = chain.EntityAt(key, number);
            return ResultWrapper<JsonElement?>.Success(JsonSerializer.SerializeToElement(found && entity is not null));
        }
        return Native("arkiv_debugEntityExists", Params(key.ToString(), requested));
    }

    public ResultWrapper<JsonElement?> arkiv_query(string query, JsonElement? options = null) =>
        Native("arkiv_query", Params(query, options));

    public ResultWrapper<JsonElement?> arkiv_getEntityCount(JsonElement? request = null) =>
        Native("arkiv_getEntityCount", Params(request));

    public ResultWrapper<JsonElement?> arkiv_getBlockTiming()
    {
        BlockHeader head = blockTree.Head?.Header ?? throw new InvalidOperationException("no head");
        BlockHeader? parent = head.Number == 0 ? null : blockTree.FindHeader(head.ParentHash!, BlockTreeLookupOptions.None);
        return ResultWrapper<JsonElement?>.Success(JsonSerializer.SerializeToElement(new Dictionary<string, ulong>
        {
            ["current_block"] = head.Number,
            ["current_block_time"] = head.Timestamp,
            ["duration"] = parent is null ? 0 : head.Timestamp - parent.Timestamp,
        }));
    }

    /// <summary>
    /// The block argument as arkiv-reth takes it — a JSON number (serde's <c>Option&lt;u64&gt;</c>) — and, as every
    /// <c>eth_*</c> method takes one, a hex quantity. A <c>string</c> parameter is how Nethermind's binder hands over
    /// both: a string's value, or a number's raw text.
    /// </summary>
    public static bool TryBlock(string? block, out ulong? number)
    {
        number = null;
        if (string.IsNullOrEmpty(block) || block == "latest") return true;
        if (block.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(block.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hex)) return false;
            number = hex;
            return true;
        }
        if (!ulong.TryParse(block, NumberStyles.None, CultureInfo.InvariantCulture, out ulong n)) return false;
        number = n;
        return true;
    }

    private static ResultWrapper<JsonElement?> InvalidBlock(string? block) =>
        ResultWrapper<JsonElement?>.Fail($"invalid block param: {block}", ErrorCodes.InvalidParams);

    /// <summary>An entity is live at a block while its expiry is after it, as <c>arkiv-reth-rpc</c>'s <c>is_live</c>.</summary>
    private static bool IsLive(JsonElement entity, ulong block) =>
        entity.TryGetProperty("expiresAt", out JsonElement expiresAt)
        && ulong.Parse(expiresAt.GetString()!.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) > block;

    private static string Params(params object?[] values)
    {
        int length = values.Length;
        while (length > 0 && values[length - 1] is null) length--;
        return JsonSerializer.Serialize(values[..length]);
    }

    private ResultWrapper<JsonElement?> Native(string method, string parameters)
    {
        JsonElement response = chain.Rpc(method, parameters);
        if (response.TryGetProperty("result", out JsonElement result))
        {
            return ResultWrapper<JsonElement?>.Success(result.ValueKind == JsonValueKind.Null ? null : result);
        }
        JsonElement error = response.GetProperty("error");
        string message = error.GetProperty("message").GetString() ?? "error";
        int code = error.GetProperty("code").GetInt32();
        return error.TryGetProperty("data", out JsonElement data)
            ? ResultWrapper<JsonElement?>.Fail(message, code, data)
            : ResultWrapper<JsonElement?>.Fail(message, code);
    }
}
