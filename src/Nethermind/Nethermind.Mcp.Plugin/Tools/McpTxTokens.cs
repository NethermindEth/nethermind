// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

/// <summary>Counts, across the logs passed to <see cref="McpTxTokens.Extract"/>, what could not be handed out as movements.</summary>
internal sealed class McpTokenTally
{
    /// <summary>Gets the movements that were counted but not materialized, beyond <see cref="McpTxTokens.MaxBatchMovements"/> in an ERC-1155 batch.</summary>
    public int Omitted { get; internal set; }

    /// <summary>Gets the ERC-1155 <c>TransferBatch</c> logs whose data or topics were malformed, so their transfers are unknown.</summary>
    public int Undecodable { get; internal set; }
}

/// <summary>Lets <see cref="McpTxTokens.LookUp"/> read token metadata on a separate module, so it can stop waiting for a slow read.</summary>
/// <param name="Rent">Rents an eth module and the lease that returns it; a <see langword="null"/> module means none is available.</param>
/// <param name="Track">Tracks work abandoned while still running, so the calling tool keeps its concurrency slot until it finishes.</param>
internal sealed record McpDetachedEth(Func<Task<(IEthRpcModule? Module, IDisposable? Lease)>> Rent, Action<Task> Track);

/// <summary>An ABI array value cut to its first entries for display; <see cref="Total"/> is its full length.</summary>
internal sealed class McpTruncatedList(int capacity, int total) : List<string>(capacity)
{
    /// <summary>Gets the number of entries of the full array.</summary>
    public int Total => total;
}

/// <summary>Extracts token movements from logs via <see cref="McpKnownAbi"/> and formats them with token metadata.</summary>
internal static class McpTxTokens
{
    /// <summary>The most movements materialized per ERC-1155 <c>TransferBatch</c> log; the rest are only counted in <see cref="McpTokenTally.Omitted"/>.</summary>
    /// <remarks>Callers display at most this many transfers anyway, and a batch can hold hundreds of thousands of ids.</remarks>
    public const int MaxBatchMovements = 50;

    /// <summary>The most ids and values shown per <c>TransferBatch</c> log in decoded JSON.</summary>
    public const int MaxBatchDisplayedEntries = 50;

    private const string TransferBatchSignature = "TransferBatch(address,address,address,uint256[],uint256[])";
    private const int WordSize = 32;
    private static readonly Hash256 TransferBatchTopic = Keccak.Compute(TransferBatchSignature);
    private static readonly TimeSpan LookupPollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>Returns a note for <paramref name="undecodable"/> logs counted in <see cref="McpTokenTally.Undecodable"/>, or <see langword="null"/> if there are none.</summary>
    public static string? UndecodableNote(int undecodable) => undecodable == 0
        ? null
        : $"{undecodable} ERC-1155 TransferBatch log(s) could not be decoded (malformed data or topics); their token transfers are not counted.";

    /// <summary>Appends the token movements described by <paramref name="log"/>, if it is a known transfer, wrap or unwrap event.</summary>
    /// <returns>The decoded log, or <see langword="null"/> if it matches no known event or is malformed.</returns>
    /// <param name="log">The log.</param>
    /// <param name="sink">Receives the movements.</param>
    /// <param name="wrappedNativeToken">The chain's wrapped native token; <c>Deposit</c>/<c>Withdrawal</c> of other contracts are not movements.</param>
    /// <param name="tally">
    /// Receives the count of batch movements beyond <see cref="MaxBatchMovements"/>, which are not added to <paramref name="sink"/>,
    /// and of malformed batches; callers must add both to what they report.
    /// </param>
    public static McpDecodedLog? Extract(LogEntry log, List<McpTokenMovement> sink, Address? wrappedNativeToken, McpTokenTally? tally = null)
    {
        if (log.Topics is { Length: > 0 } topics && topics[0] == TransferBatchTopic)
        {
            // Decoded here rather than by the generic codec: a valid batch can exceed its value limit, and every id must be counted.
            McpDecodedLog? batch = ExtractBatch(log, sink, tally);
            if (batch is null && tally is not null) tally.Undecodable++;
            return batch;
        }

        McpDecodedLog? decoded = McpKnownAbi.TryDecodeLog(log, wrappedNativeToken);
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
            case "Deposit" when decoded.Standard == "WETH" && p.Count >= 2 && TryAddress(p[0].Value, out Address? to) && TryAmount(p[1].Value, out UInt256 wad):
                sink.Add(new McpTokenMovement(log.Address, "WETH", Address.Zero, to, wad, null));
                break;
            case "Withdrawal" when decoded.Standard == "WETH" && p.Count >= 2 && TryAddress(p[0].Value, out Address? from) && TryAmount(p[1].Value, out UInt256 wad):
                sink.Add(new McpTokenMovement(log.Address, "WETH", from, Address.Zero, wad, null));
                break;
        }

        return decoded;
    }

    /// <summary>
    /// Decodes an ERC-1155 <c>TransferBatch(address indexed operator, address indexed from, address indexed to, uint256[] ids, uint256[] values)</c>
    /// by walking its two arrays in place: the first <see cref="MaxBatchMovements"/> entries become movements and the rest are
    /// counted, so memory stays bounded however large the batch is.
    /// </summary>
    /// <returns>The decoded log with at most <see cref="MaxBatchDisplayedEntries"/> ids and values, or <see langword="null"/> if it is malformed.</returns>
    private static McpDecodedLog? ExtractBatch(LogEntry log, List<McpTokenMovement> sink, McpTokenTally? tally)
    {
        ReadOnlySpan<byte> data = log.Data;
        if (log.Topics.Length != 4
            || !TryTopicAddress(log.Topics[1], out string? operatorText, out _)
            || !TryTopicAddress(log.Topics[2], out string? fromText, out Address? from)
            || !TryTopicAddress(log.Topics[3], out string? toText, out Address? to)
            || !TryArray(data, 0, out int idsStart, out int count)
            || !TryArray(data, 1, out int valuesStart, out int valuesCount)
            || count != valuesCount)
        {
            return null;
        }

        int materialized = Math.Min(count, MaxBatchMovements);
        for (int i = 0; i < materialized; i++)
        {
            UInt256 id = new(data.Slice(idsStart + i * WordSize, WordSize), isBigEndian: true);
            UInt256 value = new(data.Slice(valuesStart + i * WordSize, WordSize), isBigEndian: true);
            sink.Add(new McpTokenMovement(log.Address, McpKnownAbi.Erc1155, from, to, value, id));
        }

        if (tally is not null) tally.Omitted += count - materialized;

        int shown = Math.Min(count, MaxBatchDisplayedEntries);
        McpTruncatedList ids = new(shown, count);
        McpTruncatedList values = new(shown, count);
        for (int i = 0; i < shown; i++)
        {
            ids.Add(WordText(data.Slice(idsStart + i * WordSize, WordSize)));
            values.Add(WordText(data.Slice(valuesStart + i * WordSize, WordSize)));
        }

        return new McpDecodedLog("TransferBatch", TransferBatchSignature, McpKnownAbi.Erc1155,
        [
            new McpDecodedParam("operator", "address", true, operatorText),
            new McpDecodedParam("from", "address", true, fromText),
            new McpDecodedParam("to", "address", true, toText),
            new McpDecodedParam("ids", "uint256[]", false, ids),
            new McpDecodedParam("values", "uint256[]", false, values)
        ]);

        static string WordText(ReadOnlySpan<byte> word) => new BigInteger(word, isUnsigned: true, isBigEndian: true).ToString(CultureInfo.InvariantCulture);
    }

    // An indexed address: a topic with zero padding.
    private static bool TryTopicAddress(Hash256? topic, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Address? address)
    {
        text = null;
        address = null;
        if (topic is null || !McpAbiCodec.TryDecodeWord(McpAbiType.Address, topic.Bytes, out object? value) || value is not string decoded)
        {
            return false;
        }

        text = decoded;
        address = new Address(topic.Bytes[12..]);
        return true;
    }

    // Locates the uint256[] whose offset is head word `index`, with the codec's rules: a word-aligned offset past the
    // two-word head, and every entry inside the data.
    private static bool TryArray(ReadOnlySpan<byte> data, int index, out int start, out int count)
    {
        start = 0;
        count = 0;
        if (data.Length < 2 * WordSize
            || !TryReadInt(data.Slice(index * WordSize, WordSize), out int offset)
            || offset % WordSize != 0 || offset < 2 * WordSize || offset > data.Length - WordSize
            || !TryReadInt(data.Slice(offset, WordSize), out count)
            || count > (data.Length - offset - WordSize) / WordSize)
        {
            return false;
        }

        start = offset + WordSize;
        return true;
    }

    private static bool TryReadInt(ReadOnlySpan<byte> word, out int value)
    {
        value = 0;
        if (word[..^4].ContainsAnyExcept((byte)0) || word[^4] >= 0x80)
        {
            return false;
        }

        value = (word[^4] << 24) | (word[^3] << 16) | (word[^2] << 8) | word[^1];
        return true;
    }

    /// <summary>Looks up metadata for at most <paramref name="maxLookups"/> distinct tokens, in order of first appearance.</summary>
    /// <remarks>
    /// <paramref name="stop"/> is checked before every metadata RPC, not only between tokens. Those RPCs cannot be cancelled, so
    /// with <paramref name="detached"/> they run on their own task and module and the caller stops waiting once
    /// <paramref name="stop"/> returns <see langword="true"/>: the analysis is then returned on time with raw amounts, and the
    /// abandoned read is tracked until its in-flight call returns.
    /// </remarks>
    /// <param name="metadata">The metadata reader.</param>
    /// <param name="eth">The rented eth module, used when <paramref name="detached"/> is not given or cannot rent a module.</param>
    /// <param name="tokens">The token addresses, possibly repeated.</param>
    /// <param name="maxLookups">The most distinct tokens to look up.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <param name="stop">Returns <see langword="true"/> once the caller's time budget is spent; the remaining tokens are then skipped.</param>
    /// <param name="detached">Rents a separate module and tracks abandoned work, so a slow read can be left behind at the deadline.</param>
    /// <returns>The metadata found, and how many tokens were not looked up (including a lookup interrupted by <paramref name="stop"/>).</returns>
    public static (Dictionary<AddressAsKey, McpTokenInfo> Tokens, int Skipped) LookUp(
        McpTokenMetadata metadata, IEthRpcModule eth, IEnumerable<Address> tokens, int maxLookups, CancellationToken cancellationToken,
        Func<bool>? stop = null, McpDetachedEth? detached = null)
    {
        List<Address> pending = [];
        HashSet<AddressAsKey> seen = [];
        int beyondLimit = 0;
        foreach (Address token in tokens)
        {
            if (!seen.Add(token))
            {
                continue;
            }

            if (pending.Count < maxLookups) pending.Add(token);
            else beyondLimit++;
        }

        TokenLookup lookup = new(metadata, pending, stop, cancellationToken);
        if (pending.Count > 0 && !lookup.Spent())
        {
            if (detached is null || stop is null)
            {
                lookup.Run(eth);
            }
            else if (!lookup.RunDetached(eth, detached))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return lookup.Result(beyondLimit);
    }

    // The state of one LookUp, shared with its worker task when detached; Result is consistent once the caller stopped waiting.
    private sealed class TokenLookup(McpTokenMetadata metadata, List<Address> pending, Func<bool>? stop, CancellationToken cancellationToken)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<AddressAsKey, McpTokenInfo> _found = [];
        private int _next;
        private bool _abandoned;

        public bool Spent() => Volatile.Read(ref _abandoned) || cancellationToken.IsCancellationRequested || stop?.Invoke() == true;

        // Reads the remaining tokens in order until done or spent.
        public void Run(IEthRpcModule eth)
        {
            while (true)
            {
                Address token;
                lock (_lock)
                {
                    if (_next >= pending.Count) return;
                    token = pending[_next];
                }

                // Metadata is read at the head: it rarely changes, and old blocks may have no state on a pruned node.
                if (Spent() || !metadata.TryGet(eth, token, BlockParameter.Latest, Spent, out McpTokenInfo? info))
                {
                    return;
                }

                lock (_lock)
                {
                    if (_abandoned) return;
                    if (info is not null) _found[token] = info;
                    _next++;
                }
            }
        }

        // Runs the reads on a worker with its own module, waiting until they finish or the budget is spent.
        // Returns false if the worker was abandoned (it is then tracked until it finishes).
        public bool RunDetached(IEthRpcModule eth, McpDetachedEth detached)
        {
            Task work = Task.Run(async () =>
            {
                (IEthRpcModule? module, IDisposable? lease) = await detached.Rent();
                using (lease)
                {
                    if (module is not null) Run(module);
                }
            }, CancellationToken.None);

            Task[] waitOn = [work];
            while (Task.WaitAny(waitOn, LookupPollInterval) < 0)
            {
                if (Spent())
                {
                    lock (_lock) _abandoned = true;
                    detached.Track(work);
                    return false;
                }
            }

            // A failed rental is not an error of the tool: the reads fall back to the caller's module below.
            _ = work.Exception;

            // Without a separate module (rental failed or unavailable) the remaining tokens are read on the caller's.
            Run(eth);
            return true;
        }

        public (Dictionary<AddressAsKey, McpTokenInfo> Tokens, int Skipped) Result(int beyondLimit)
        {
            lock (_lock)
            {
                _abandoned = true;
                return (new Dictionary<AddressAsKey, McpTokenInfo>(_found), beyondLimit + pending.Count - _next);
            }
        }
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
            ["token"] = McpEthHelpers.Checksum(movement.Token),
            ["standard"] = movement.Standard,
            ["from"] = McpEthHelpers.Checksum(movement.From),
            ["to"] = McpEthHelpers.Checksum(movement.To),
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
                ["address"] = McpEthHelpers.Checksum(holder),
                ["token"] = McpEthHelpers.Checksum(token),
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

    /// <summary>Returns the non-zero net change of each fungible token held by <paramref name="holder"/>, in order of first appearance.</summary>
    public static List<(Address Token, BigInteger Change)> NetFor(IReadOnlyList<McpTokenMovement> movements, Address holder)
    {
        List<(Address Token, BigInteger Change)> result = [];
        foreach (McpTokenMovement movement in movements)
        {
            if (!movement.IsFungible || (movement.From != holder && movement.To != holder) || movement.From == movement.To)
            {
                continue;
            }

            BigInteger delta = movement.To == holder ? (BigInteger)movement.Amount : -(BigInteger)movement.Amount;
            int index = result.FindIndex(entry => entry.Token == movement.Token);
            if (index < 0) result.Add((movement.Token, delta));
            else result[index] = (movement.Token, result[index].Change + delta);
        }

        result.RemoveAll(static entry => entry.Change.IsZero);
        return result;
    }

    /// <summary>Converts a decoded log into JSON: <c>{event, signature, standard?, params: [{name, type, value}]}</c>.</summary>
    public static JsonObject DecodedJson(McpDecodedLog decoded)
    {
        JsonArray parameters = [];
        foreach (McpDecodedParam p in decoded.Params)
        {
            JsonObject parameter = new()
            {
                ["name"] = p.Name,
                ["type"] = p.Type,
                ["value"] = p.Value is null ? null : JsonSerializer.SerializeToNode(p.Value, p.Value.GetType(), EthereumJsonSerializer.JsonOptions)
            };
            if (p.Value is McpTruncatedList { } list && list.Total > list.Count)
            {
                parameter["length"] = list.Total;
                parameter["entriesOmitted"] = list.Total - list.Count;
            }

            parameters.Add(parameter);
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
