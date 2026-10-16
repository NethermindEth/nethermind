// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.Subscribe;

/// <summary>Parameters of a <c>newHeads</c> subscription.</summary>
/// <remarks>
/// Accepts everything <see cref="TransactionsOption"/> accepts (a boolean, or <c>{"includeTransactions": bool}</c>), plus
/// two object properties:
/// <list type="bullet">
///   <item><c>headerOnly</c>: the header only, without transaction hashes and withdrawals;</item>
///   <item><c>optimistic</c>: announce a block once it is validated and extends the head, before it becomes canonical.</item>
/// </list>
/// A property that is not given keeps the node's default (<see cref="IJsonRpcConfig.NewHeadsHeaderOnly"/>,
/// <see cref="IJsonRpcConfig.NewHeadsOptimistic"/>).
/// </remarks>
public sealed class NewHeadsOption : IJsonRpcParam
{
    public bool IncludeTransactions { get; private set; }

    public bool? HeaderOnly { get; private set; }

    public bool? Optimistic { get; private set; }

    public void ReadJson(JsonElement jsonValue, JsonSerializerOptions options)
    {
        TransactionsOption transactions = new();
        transactions.ReadJson(jsonValue, options);
        IncludeTransactions = transactions.IncludeTransactions;

        if (jsonValue.ValueKind != JsonValueKind.Object) return;

        HeaderOnly = ReadFlag(jsonValue, "headerOnly"u8, "headerOnly");
        Optimistic = ReadFlag(jsonValue, "optimistic"u8, "optimistic");
    }

    private static bool? ReadFlag(JsonElement value, ReadOnlySpan<byte> utf8Name, string name)
    {
        if (!value.TryGetProperty(utf8Name, out JsonElement flag)) return null;

        return flag.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new JsonException($"newHeads parameter '{name}' must be a boolean.")
        };
    }
}
