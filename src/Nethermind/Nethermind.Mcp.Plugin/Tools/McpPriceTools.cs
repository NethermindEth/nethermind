// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>A USD price observed at a Chainlink AggregatorV3 proxy.</summary>
internal sealed record McpPriceQuote(McpPriceFeed Feed, BigInteger Answer, int Decimals, BigInteger RoundId,
    ulong UpdatedAt, ulong AgeSeconds, bool Stale)
{
    public string PriceUsd => McpTokenMetadata.FormatUnits(Answer, Decimals);
    public string PricedVia => Feed.Assumption is null ? $"{Feed.Symbol}/USD" : $"{Feed.Symbol}/USD ({Feed.Assumption})";

    public string ValueUsd(UInt256 amount, int tokenDecimals) => ValueUsd((BigInteger)amount, tokenDecimals);

    public string ValueUsd(BigInteger amount, int tokenDecimals)
    {
        BigInteger numerator = BigInteger.Abs(amount) * Answer;
        if (numerator.IsZero) return "0";
        BigInteger divisor = BigInteger.Pow(10, tokenDecimals + Decimals);
        string sign = amount.Sign < 0 ? "-" : string.Empty;
        if (numerator >= divisor)
        {
            BigInteger cents = (numerator * 100 + divisor / 2) / divisor;
            return sign + (cents / 100).ToString(CultureInfo.InvariantCulture) + "." + (cents % 100).ToString("D2", CultureInfo.InvariantCulture);
        }

        int places = 2;
        BigInteger scaled = numerator * 100;
        while (scaled < divisor * 1000) { scaled *= 10; places++; }
        BigInteger rounded = (scaled + divisor / 2) / divisor;
        string formatted = McpTokenMetadata.FormatUnits(rounded, places);
        return sign + (formatted == "1" ? "1.00" : formatted);
    }
}

internal sealed record McpPriceResult(McpPriceQuote? Quote, string Reason);

/// <summary>Reads verified on-chain Chainlink feeds with bounded eth calls.</summary>
internal sealed class McpPriceReader(McpChainProfile profile, ILogManager logManager, TimeProvider? timeProvider = null)
{
    private static readonly byte[] LatestRoundSelector = [0xfe, 0xaf, 0x96, 0x8c];
    private static readonly byte[] DecimalsSelector = [0x31, 0x3c, 0xe5, 0x67];
    private readonly ILogger _logger = logManager.GetClassLogger<McpPriceReader>();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    internal static TimeSpan OptionalBudget(int timeoutMilliseconds, Stopwatch clock, TimeSpan? remainingDeadline = null)
    {
        double margin = Math.Max(250, timeoutMilliseconds * 0.1);
        double remaining = Math.Min(timeoutMilliseconds - clock.Elapsed.TotalMilliseconds, remainingDeadline?.TotalMilliseconds ?? timeoutMilliseconds);
        if (remaining < 2 * margin) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(Math.Min(1000, timeoutMilliseconds * 0.25), remaining - margin)));
    }

    internal async Task<(McpPriceQuote? Quote, string Reason)> ReadOptionalAsync(McpToolExecutor executor,
        string token, BlockParameter block, TimeSpan budget, CancellationToken cancellationToken)
    {
        Dictionary<string, McpPriceResult> batch = await ReadOptionalBatchAsync(executor, [token], block, budget, cancellationToken);
        McpPriceResult result = batch[token];
        return (result.Quote, result.Reason);
    }

    internal async Task<Dictionary<string, McpPriceResult>> ReadOptionalBatchAsync(McpToolExecutor executor,
        IEnumerable<string> tokens, BlockParameter block, TimeSpan budget, CancellationToken cancellationToken, bool pinnedHead = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, McpPriceFeed?> requests = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<AddressAsKey, McpPriceFeed> feeds = [];
        foreach (string token in tokens)
        {
            McpPriceFeed? feed = profile.IsTestnet ? null : profile.PriceFeed(token);
            requests[token] = feed;
            if (feed is not null) feeds.TryAdd(feed.Address, feed);
        }
        if (budget <= TimeSpan.Zero || feeds.Count == 0) return Finish([], DeadlineNote);

        Stopwatch clock = Stopwatch.StartNew();
        // The worker owns its lease until the last synchronous RPC returns, even if this caller stops waiting.
        Task<Dictionary<AddressAsKey, McpPriceResult>> work = Task.Run(async () =>
        {
            Dictionary<AddressAsKey, McpPriceResult> results = [];
            try
            {
                if (Stopped()) return results;
                using ModuleLease<IEthRpcModule> lease = await executor.RentAsync<IEthRpcModule>(nameof(IEthRpcModule.eth_call));
                if (lease.Module is null) return Failed("The eth_call module is unavailable for the price feed.");
                if (!TryResolveReference(lease.Module, block, Stopped, pinnedHead, out PriceReference reference, out string referenceError)) return Failed(referenceError);
                foreach ((AddressAsKey key, McpPriceFeed feed) in feeds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Stopped()) break;
                    try
                    {
                        TryReadFeed(lease.Module, feed, block, Stopped, reference, out McpPriceQuote? quote, out string reason);
                        results[key] = new(quote, reason);
                    }
                    catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (_logger.IsDebug) _logger.Debug($"MCP optional price lookup cancelled: {error.Message}");
                        results[key] = new(null, "The price feed cancelled its lookup.");
                    }
                }
                return results;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (_logger.IsDebug) _logger.Debug($"MCP optional price lookup failed: {error.Message}");
                return Failed("The on-chain price feed is unavailable on this node.");
            }

            Dictionary<AddressAsKey, McpPriceResult> Failed(string reason)
            {
                foreach (AddressAsKey key in feeds.Keys) results.TryAdd(key, new(null, reason));
                return results;
            }
        });
        try
        {
            return Finish(await work.WaitAsync(budget, cancellationToken), DeadlineNote);
        }
        catch (TimeoutException)
        {
            executor.TrackDetached(work);
            return Finish([], DeadlineNote);
        }
        catch (OperationCanceledException)
        {
            executor.TrackDetached(work);
            throw;
        }

        bool Stopped() => clock.Elapsed >= budget || cancellationToken.IsCancellationRequested;

        Dictionary<string, McpPriceResult> Finish(Dictionary<AddressAsKey, McpPriceResult> results, string fallback)
        {
            Dictionary<string, McpPriceResult> output = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string asset, McpPriceFeed? feed) in requests)
            {
                McpPriceResult price = feed is null
                    ? new(null, profile.IsTestnet ? "USD values are not available on testnets." : "No verified USD price feed is configured for this token on this chain.")
                    : results.TryGetValue(feed.Address, out McpPriceResult? result) ? result : new(null, fallback);
                output[asset] = price.Quote is { Stale: false } ? price : price with
                {
                    Reason = price.Reason == DeadlineNote ? DeadlineNote : $"USD omitted: {price.Reason}"
                };
            }
            return output;
        }
    }

    private const string DeadlineNote = "USD omitted because the price lookup deadline was reached.";

    public bool TryRead(IEthRpcModule eth, string token, BlockParameter block, Func<bool>? stop,
        out McpPriceQuote? quote, out string reason)
    {
        quote = null;
        if (profile.IsTestnet)
        {
            reason = "USD values are not available on testnets.";
            return false;
        }

        McpPriceFeed? feed = profile.PriceFeed(token);
        if (feed is null)
        {
            reason = "No verified USD price feed is configured for this token on this chain.";
            return false;
        }

        return TryReadFeed(eth, feed, block, stop, out quote, out reason);
    }

    internal bool TryReadFeed(IEthRpcModule eth, McpPriceFeed feed, BlockParameter block, Func<bool>? stop,
        out McpPriceQuote? quote, out string reason) => TryReadFeed(eth, feed, block, stop, null, out quote, out reason);

    private bool TryReadFeed(IEthRpcModule eth, McpPriceFeed feed, BlockParameter block, Func<bool>? stop, PriceReference? resolved,
        out McpPriceQuote? quote, out string reason)
    {
        quote = null;
        reason = "The Chainlink feed did not return valid price data at this block.";
        try
        {
            if (stop?.Invoke() == true)
            {
                reason = DeadlineNote;
                return false;
            }

            PriceReference reference;
            if (resolved is { } known) reference = known;
            else if (!TryResolveReference(eth, block, stop, false, out reference, out reason)) return false;
            block = reference.Block;
            reason = "The Chainlink feed did not return valid price data at this block.";

            bool transient = false;
            byte[]? round = McpTokenMetadata.Call(eth, feed.Address, LatestRoundSelector, block, McpTokenMetadata.MetadataCallGas, ref transient);
            if (round is not { Length: 160 }) return false;
            if (stop?.Invoke() == true)
            {
                reason = DeadlineNote;
                return false;
            }

            byte[]? decimalsData = McpTokenMetadata.Call(eth, feed.Address, DecimalsSelector, block, McpTokenMetadata.MetadataCallGas, ref transient);
            if (decimalsData is not { Length: 32 } || decimalsData.AsSpan(0, 31).ContainsAnyExcept((byte)0) || decimalsData[31] > 36)
                return false;

            BigInteger roundId = new(round.AsSpan(0, 32), isUnsigned: true, isBigEndian: true);
            BigInteger answer = new(round.AsSpan(32, 32), isUnsigned: false, isBigEndian: true);
            BigInteger updated = new(round.AsSpan(96, 32), isUnsigned: true, isBigEndian: true);
            BigInteger answeredInRound = new(round.AsSpan(128, 32), isUnsigned: true, isBigEndian: true);
            if (answer <= 0 || answeredInRound < roundId || updated <= 0 || updated > ulong.MaxValue
                || updated > reference.Timestamp && (reference.Historical || updated - reference.Timestamp > 60))
                return false;

            ulong timestamp = (ulong)updated;
            ulong age = reference.Timestamp > timestamp ? reference.Timestamp - timestamp : 0;
            int grace = Math.Max(60, feed.HeartbeatSeconds / 20);
            quote = new McpPriceQuote(feed, answer, decimalsData[31], roundId, timestamp, age, age > (ulong)(feed.HeartbeatSeconds + grace));
            reason = quote.Stale ? $"The Chainlink price is stale at the reference time: age {age} seconds exceeds its {feed.HeartbeatSeconds}-second heartbeat plus {grace}-second grace." : string.Empty;
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (_logger.IsDebug) _logger.Debug($"MCP price feed {feed.Address} failed: {error.Message}");
            return false;
        }
    }

    private readonly record struct PriceReference(BlockParameter Block, ulong Timestamp, bool Historical);

    private bool TryResolveReference(IEthRpcModule eth, BlockParameter block, Func<bool>? stop, bool pinnedHead,
        out PriceReference reference, out string reason)
    {
        reference = default;
        reason = DeadlineNote;
        if (stop?.Invoke() == true) return false;
        bool historical = !pinnedHead && block.Type != BlockParameterType.Latest;
        ulong timestamp = (ulong)_time.GetUtcNow().ToUnixTimeSeconds();
        if (historical)
        {
            using ResultWrapper<BlockHeaderForRpc?> header = block.Type == BlockParameterType.BlockHash
                ? eth.eth_getHeaderByHash(block.BlockHash!) : eth.eth_getHeaderByNumber(block);
            if (header.Result.ResultType != ResultType.Success || header.Data is null || header.Data.Timestamp > ulong.MaxValue)
            {
                reason = "The block timestamp is unavailable; choose a block whose header this node stores.";
                return false;
            }
            timestamp = (ulong)header.Data.Timestamp;
            if (header.Data.Hash is { } hash) block = new BlockParameter(hash);
        }
        if (stop?.Invoke() == true) return false;
        reference = new PriceReference(block, timestamp, historical);
        reason = string.Empty;
        return true;
    }

}

/// <summary>Exposes verified USD prices from on-chain Chainlink feeds.</summary>
[McpServerToolType]
internal sealed class McpPriceTools(McpToolExecutor executor, McpPriceReader prices, McpChainProfile profile, IMcpConfig config) : IMcpToolSet
{
    /// <inheritdoc/>
    public IEnumerable<McpServerTool> CreateServerTools() => McpToolFactory.Create(this, _ => string.Empty, config.MaxResultSize);

    /// <summary>Reads the Chainlink USD price at a block for a known token or the native currency.</summary>
    [McpServerTool(Name = "token_price", Title = "Token price", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpToolOutputSchema("""
        {"type":"object","required":["result"],"properties":{"result":{"type":"object",
          "required":["token","priceUsd","decimals","feed","updatedAt","ageSeconds","roundId","stale","heartbeatSeconds","pricedVia"],
          "properties":{"token":{"type":"string"},"priceUsd":{"type":"string"},"decimals":{"type":"integer"},
            "feed":{"type":"string"},"updatedAt":{"type":"integer"},"ageSeconds":{"type":"integer"},
            "roundId":{"type":"string"},"stale":{"type":"boolean"},"heartbeatSeconds":{"type":"integer"},"pricedVia":{"type":"string"}}}}}
        """)]
    [Description("Reads a verified Chainlink AggregatorV3 USD price entirely on-chain. Accepts native, a well-known token symbol or its contract address. " +
        "Returns priceUsd, pricedVia (including peg assumptions), feed address, round, updatedAt, and age at the selected block (latest uses the node clock with 60 seconds of skew tolerance). Stale means older than the heartbeat plus max(60 seconds, 5%). " +
        "Stale or missing prices must not be used as current valuations. Testnets have no USD values.")]
    public Task<CallToolResult> TokenPrice(
        [Description("'native', a well-known token symbol (such as WETH or USDC), or its contract address.")] string token,
        [Description(McpEthTools.BlockSelectorDescription + " Default latest.")] string block = "latest",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64)
            return McpEthHelpers.InvalidInput("'token' must be 'native', a well-known symbol, or a 20-byte token address.");
        if (!McpToolInput.TryParseBlock(block, nameof(block), out BlockParameter? query, out string? error))
            return McpEthHelpers.InvalidInput(error);
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && !McpToolInput.TryParseAddress(token, nameof(token), out _, out error))
            return McpEthHelpers.InvalidInput(error);
        if (profile.IsTestnet)
            return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.Unavailable, "USD values are not available on testnets."));
        if (profile.PriceFeed(token) is null)
            return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.Unavailable,
                "No verified Chainlink USD price feed is configured for this token on this chain; use a listed well-known token or 'native'."));

        return executor.ExecuteAsync("token_price", nameof(IEthRpcModule.eth_call), (eth, _) =>
        {
            if (!prices.TryRead(eth, token, query, null, out McpPriceQuote? quote, out string reason))
                return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.Unavailable, reason));

            JsonObject result = new()
            {
                ["token"] = token, ["priceUsd"] = quote!.PriceUsd, ["decimals"] = quote.Decimals,
                ["feed"] = McpEthHelpers.Checksum(quote.Feed.Address), ["updatedAt"] = (long)quote.UpdatedAt,
                ["ageSeconds"] = (long)quote.AgeSeconds, ["roundId"] = quote.RoundId.ToString(),
                ["stale"] = quote.Stale, ["heartbeatSeconds"] = quote.Feed.HeartbeatSeconds, ["pricedVia"] = quote.PricedVia
            };
            return Task.FromResult(executor.Success(result));
        }, cancellationToken);
    }
}
