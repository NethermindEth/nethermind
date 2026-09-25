// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Mcp.Plugin.Tools.Abi;
using Nethermind.Serialization.Json;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>A token moved by a decoded log.</summary>
/// <param name="Token">The token contract.</param>
/// <param name="Standard"><c>ERC-20</c>, <c>ERC-721</c>, <c>ERC-1155</c> or <c>WETH</c>.</param>
/// <param name="From">The sender; the zero address for mints and wraps.</param>
/// <param name="To">The recipient; the zero address for burns and unwraps.</param>
/// <param name="Amount">The amount in the token's base units (1 for an ERC-721 token).</param>
/// <param name="TokenId">The NFT id for ERC-721 and ERC-1155 movements.</param>
internal sealed record McpTokenMovement(Address Token, string Standard, Address From, Address To, UInt256 Amount, UInt256? TokenId)
{
    /// <summary>Gets whether amounts of this token are fungible and can be summed per holder.</summary>
    public bool IsFungible => Standard is "ERC-20" or "WETH";
}

/// <summary>Extracts token movements from logs via <see cref="McpKnownAbi"/> and formats them with token metadata.</summary>
internal static class McpTxTokens
{
    /// <summary>The maximum number of ids read from one ERC-1155 <c>TransferBatch</c> log.</summary>
    private const int MaxBatchIds = 16;

    /// <summary>Appends the token movements described by <paramref name="log"/>, if it is a known transfer, wrap or unwrap event.</summary>
    /// <returns>The decoded log, or <see langword="null"/> if it matches no known event.</returns>
    public static McpDecodedLog? Extract(LogEntry log, List<McpTokenMovement> sink)
    {
        McpDecodedLog? decoded = McpKnownAbi.TryDecodeLog(log);
        if (decoded is null)
        {
            return null;
        }

        IReadOnlyList<McpDecodedParam> p = decoded.Params;
        string standard = decoded.Standard ?? "ERC-20";
        switch (decoded.Event)
        {
            case "Transfer" when p.Count >= 3 && TryAddress(p[0].Value, out Address? from) && TryAddress(p[1].Value, out Address? to) && TryAmount(p[2].Value, out UInt256 amount):
                sink.Add(standard == "ERC-721"
                    ? new McpTokenMovement(log.Address, standard, from, to, UInt256.One, amount)
                    : new McpTokenMovement(log.Address, standard, from, to, amount, null));
                break;
            case "TransferSingle" when p.Count >= 5 && TryAddress(p[1].Value, out Address? from) && TryAddress(p[2].Value, out Address? to)
                && TryAmount(p[3].Value, out UInt256 id) && TryAmount(p[4].Value, out UInt256 value):
                sink.Add(new McpTokenMovement(log.Address, "ERC-1155", from, to, value, id));
                break;
            case "TransferBatch" when p.Count >= 5 && TryAddress(p[1].Value, out Address? from) && TryAddress(p[2].Value, out Address? to)
                && p[3].Value is IList ids && p[4].Value is IList values:
                for (int i = 0; i < Math.Min(Math.Min(ids.Count, values.Count), MaxBatchIds); i++)
                {
                    if (TryAmount(ids[i], out UInt256 batchId) && TryAmount(values[i], out UInt256 batchValue))
                    {
                        sink.Add(new McpTokenMovement(log.Address, "ERC-1155", from, to, batchValue, batchId));
                    }
                }

                break;
            case "Deposit" when p.Count >= 2 && TryAddress(p[0].Value, out Address? to) && TryAmount(p[1].Value, out UInt256 wad):
                sink.Add(new McpTokenMovement(log.Address, "WETH", Address.Zero, to, wad, null));
                break;
            case "Withdrawal" when p.Count >= 2 && TryAddress(p[0].Value, out Address? from) && TryAmount(p[1].Value, out UInt256 wad):
                sink.Add(new McpTokenMovement(log.Address, "WETH", from, Address.Zero, wad, null));
                break;
        }

        return decoded;
    }

    /// <summary>Looks up metadata for at most <paramref name="maxLookups"/> distinct tokens, in order of first appearance.</summary>
    /// <returns>The metadata found, and how many tokens were not looked up.</returns>
    public static (Dictionary<AddressAsKey, McpTokenInfo> Tokens, int Skipped) LookUp(
        McpTokenMetadata metadata, IEthRpcModule eth, IEnumerable<Address> tokens, int maxLookups, CancellationToken cancellationToken)
    {
        Dictionary<AddressAsKey, McpTokenInfo> found = [];
        HashSet<AddressAsKey> seen = [];
        int skipped = 0;
        foreach (Address token in tokens)
        {
            if (!seen.Add(token))
            {
                continue;
            }

            if (seen.Count > maxLookups)
            {
                skipped++;
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Metadata is read at the head: it rarely changes, and old blocks may have no state on a pruned node.
            if (metadata.Get(eth, token, BlockParameter.Latest) is { } info)
            {
                found[token] = info;
            }
        }

        return (found, skipped);
    }

    /// <summary>Formats an amount of <paramref name="token"/>, such as <c>1250.5</c>, or the raw integer when decimals are unknown.</summary>
    public static string FormatAmount(McpTokenMovement movement, McpTokenInfo? token) =>
        movement.IsFungible && token?.Decimals is { } decimals
            ? McpTokenMetadata.FormatUnits(movement.Amount, decimals)
            : movement.Amount.ToString();

    /// <summary>Returns a display name for a token: its symbol, or its short address.</summary>
    public static string Label(Address token, McpTokenInfo? info) => info?.Symbol ?? McpTxFormat.Short(token);

    /// <summary>Converts a movement into JSON: <c>{token, symbol?, standard, from, to, amount, amountFormatted?, tokenId?}</c>.</summary>
    public static JsonObject MovementJson(McpTokenMovement movement, McpTokenInfo? info)
    {
        JsonObject json = new()
        {
            ["token"] = McpTxFormat.Checksum(movement.Token),
            ["standard"] = movement.Standard,
            ["from"] = McpTxFormat.Checksum(movement.From),
            ["to"] = McpTxFormat.Checksum(movement.To),
            ["amount"] = movement.Amount.ToString()
        };

        if (info?.Symbol is not null) json["symbol"] = info.Symbol;
        if (movement.IsFungible && info?.Decimals is not null) json["amountFormatted"] = FormatAmount(movement, info);
        if (movement.TokenId is { } id) json["tokenId"] = id.ToString();
        return json;
    }

    /// <summary>
    /// Sums fungible movements into net balance changes per holder and token, largest absolute change first, skipping the
    /// zero address (mints and burns show up on the counterparty).
    /// </summary>
    public static JsonArray NetFlows(IReadOnlyList<McpTokenMovement> movements, Dictionary<AddressAsKey, McpTokenInfo> tokens, int max)
    {
        Dictionary<(AddressAsKey Holder, AddressAsKey Token), BigInteger> net = [];
        foreach (McpTokenMovement movement in movements)
        {
            if (!movement.IsFungible)
            {
                continue;
            }

            BigInteger amount = (BigInteger)movement.Amount;
            if (movement.From != Address.Zero) Add((movement.From, movement.Token), -amount);
            if (movement.To != Address.Zero) Add((movement.To, movement.Token), amount);
        }

        List<KeyValuePair<(AddressAsKey Holder, AddressAsKey Token), BigInteger>> ordered = [];
        foreach (KeyValuePair<(AddressAsKey Holder, AddressAsKey Token), BigInteger> entry in net)
        {
            if (!entry.Value.IsZero) ordered.Add(entry);
        }

        ordered.Sort(static (a, b) => BigInteger.Abs(b.Value).CompareTo(BigInteger.Abs(a.Value)));
        JsonArray result = [];
        for (int i = 0; i < ordered.Count && i < max; i++)
        {
            ((AddressAsKey holder, AddressAsKey token), BigInteger change) = ordered[i];
            tokens.TryGetValue(token, out McpTokenInfo? info);
            JsonObject json = new()
            {
                ["address"] = McpTxFormat.Checksum(holder),
                ["token"] = McpTxFormat.Checksum(token),
                ["change"] = change.ToString()
            };

            if (info?.Symbol is not null) json["symbol"] = info.Symbol;
            if (info?.Decimals is { } decimals) json["changeFormatted"] = McpTxFormat.SignedUnits(change, decimals);
            result.Add(json);
        }

        return result;

        void Add((AddressAsKey, AddressAsKey) key, BigInteger delta) =>
            net[key] = net.TryGetValue(key, out BigInteger current) ? current + delta : delta;
    }

    /// <summary>Converts a decoded log into JSON: <c>{event, signature, standard?, params: [{name, type, value}]}</c>.</summary>
    public static JsonObject DecodedJson(McpDecodedLog decoded)
    {
        JsonArray parameters = [];
        foreach (McpDecodedParam p in decoded.Params)
        {
            parameters.Add(new JsonObject
            {
                ["name"] = p.Name,
                ["type"] = p.Type,
                ["value"] = p.Value is null ? null : JsonSerializer.SerializeToNode(p.Value, p.Value.GetType(), EthereumJsonSerializer.JsonOptions)
            });
        }

        JsonObject json = new() { ["event"] = decoded.Event, ["signature"] = decoded.Signature };
        if (decoded.Standard is not null) json["standard"] = decoded.Standard;
        json["params"] = parameters;
        return json;
    }

    private static bool TryAddress(object? value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Address? address)
    {
        address = value switch
        {
            Address a => a,
            string s when McpToolInput.TryParseAddress(s, "value", out Address? parsed, out _) => parsed,
            _ => null
        };
        return address is not null;
    }

    private static bool TryAmount(object? value, out UInt256 amount)
    {
        switch (value)
        {
            case UInt256 u:
                amount = u;
                return true;
            case BigInteger b when b.Sign >= 0:
                amount = (UInt256)b;
                return true;
            case ulong l:
                amount = l;
                return true;
            case long l when l >= 0:
                amount = (ulong)l;
                return true;
            case int i when i >= 0:
                amount = (ulong)i;
                return true;
            case string s:
                return McpToolInput.TryParseUInt256(s, "value", out amount, out _);
            default:
                amount = default;
                return false;
        }
    }
}
