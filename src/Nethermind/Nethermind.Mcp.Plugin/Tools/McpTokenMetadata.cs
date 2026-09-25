// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Numerics;
using System.Text;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Mcp.Plugin.Tools.Abi;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Token metadata read from the contract; any field may be missing for non-standard tokens.</summary>
public sealed record McpTokenInfo(Address Address, string? Name, string? Symbol, byte? Decimals);

/// <summary>Reads and caches ERC-20/721 metadata (<c>name</c>, <c>symbol</c>, <c>decimals</c>) via <c>eth_call</c>.</summary>
/// <remarks>
/// <para>Callers pass an eth module they already rented. Each metadata call is bounded by <see cref="MetadataCallGas"/>.
/// Both ABI <c>string</c> and legacy <c>bytes32</c> results (such as MKR's) are accepted; texts are stripped of control
/// characters and capped at <see cref="MaxTextLength"/> characters, since they are untrusted contract output shown to an LLM.</para>
/// <para>Results read at the head are cached per chain and address in a bounded LRU of <see cref="CacheCapacity"/> entries;
/// reads at older blocks bypass the cache, since a token may not exist yet or may have been upgraded since. Results
/// affected by a transient failure (state not available, node busy, unexpected error) are returned but not cached;
/// reverts and malformed return data are permanent and cached as missing fields.</para>
/// </remarks>
/// <param name="logManager">Logs unexpected failures at debug level.</param>
/// <param name="blockFinder">Recognizes head blocks passed by hash or number; without it only <c>latest</c> reads are cached.</param>
public sealed class McpTokenMetadata(ILogManager logManager, IBlockFinder? blockFinder = null)
{
    /// <summary>The maximum number of cached tokens.</summary>
    public const int CacheCapacity = 4096;

    /// <summary>The gas limit of each metadata <c>eth_call</c>.</summary>
    public const ulong MetadataCallGas = 150_000;

    /// <summary>The maximum length of a returned name or symbol.</summary>
    public const int MaxTextLength = 64;

    private static readonly byte[] NameSelector = [0x06, 0xfd, 0xde, 0x03];
    private static readonly byte[] SymbolSelector = [0x95, 0xd8, 0x9b, 0x41];
    private static readonly byte[] DecimalsSelector = [0x31, 0x3c, 0xe5, 0x67];
    private static readonly McpAbiParam[] StringOutput = [new(string.Empty, McpAbiType.String)];

    private readonly Lock _lock = new();
    private readonly Dictionary<(ulong ChainId, AddressAsKey Address), LinkedListNode<CacheEntry>> _cache = [];
    private readonly LinkedList<CacheEntry> _order = new();
    private readonly ILogger _logger = logManager.GetClassLogger<McpTokenMetadata>();

    /// <summary>Creates the metadata reader without logging.</summary>
    public McpTokenMetadata() : this(LimboLogs.Instance, null)
    {
    }

    /// <summary>Gets the number of cached tokens.</summary>
    public int CachedCount
    {
        get
        {
            lock (_lock) return _cache.Count;
        }
    }

    /// <summary>Returns the token metadata at <paramref name="block"/>, or <see langword="null"/> if <paramref name="token"/> has no code.</summary>
    /// <remarks>Never throws for contract misbehaviour; returns <see langword="null"/> also when the code cannot be read (for example state not available).</remarks>
    public McpTokenInfo? Get(IEthRpcModule eth, Address token, BlockParameter block)
    {
        ResultWrapper<ulong> chainIdResult = eth.eth_chainId();
        ulong chainId = chainIdResult.Result.ResultType == ResultType.Success ? chainIdResult.Data : 0;
        (ulong, AddressAsKey) key = (chainId, token);
        bool cacheable = IsHead(block);
        lock (_lock)
        {
            if (cacheable && _cache.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Info;
            }
        }

        try
        {
            using ResultWrapper<byte[]> code = eth.eth_getCode(token, block);
            if (code.Result.ResultType != ResultType.Success || code.Data is not { Length: > 0 })
            {
                return null;
            }

            bool transient = false;
            string? name = ReadText(eth, token, block, NameSelector, ref transient);
            string? symbol = ReadText(eth, token, block, SymbolSelector, ref transient);
            byte? decimals = ReadDecimals(eth, token, block, ref transient);
            McpTokenInfo info = new(token, name, symbol, decimals);
            if (cacheable && !transient)
            {
                Add(key, info);
            }

            return info;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_logger.IsDebug) _logger.Debug($"MCP token metadata for {token} failed: {e.Message}");
            return null;
        }
    }

    private bool IsHead(BlockParameter block)
    {
        if (block.Type == BlockParameterType.Latest)
        {
            return true;
        }

        BlockHeader? head = blockFinder?.Head?.Header;
        return head is not null && (block.BlockHash is { } hash ? hash == head.Hash : block.BlockNumber == head.Number);
    }

    /// <summary>Formats a raw integer amount with <paramref name="decimals"/> as a decimal string, such as <c>1.5</c>.</summary>
    /// <remarks>Exact at any size; trailing zeros are trimmed and a negative <paramref name="decimals"/> is treated as 0.</remarks>
    public static string FormatUnits(UInt256 amount, int decimals) => FormatUnits((BigInteger)amount, decimals);

    /// <summary>Formats a signed raw integer amount with <paramref name="decimals"/>, such as <c>-1.5</c>.</summary>
    public static string FormatUnits(BigInteger amount, int decimals)
    {
        bool negative = amount.Sign < 0;
        string digits = BigInteger.Abs(amount).ToString(CultureInfo.InvariantCulture);
        if (decimals > 0)
        {
            if (digits.Length <= decimals)
            {
                digits = new string('0', decimals - digits.Length + 1) + digits;
            }

            int point = digits.Length - decimals;
            ReadOnlySpan<char> fraction = digits.AsSpan(point).TrimEnd('0');
            digits = fraction.Length == 0 ? digits[..point] : string.Concat(digits.AsSpan(0, point), ".", fraction);
        }

        return negative ? "-" + digits : digits;
    }

    /// <summary>Formats a decimal integer string (as produced by <see cref="McpAbiCodec"/>) with <paramref name="decimals"/>, or returns <see langword="null"/> if it is not an integer.</summary>
    public static string? FormatUnits(string? decimalAmount, int decimals) =>
        decimalAmount is { Length: > 0 and <= 80 } && BigInteger.TryParse(decimalAmount, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger value)
            ? FormatUnits(value, decimals)
            : null;

    /// <summary>Decodes a <c>name()</c>/<c>symbol()</c> result: an ABI <c>string</c> or a legacy zero-padded <c>bytes32</c>.</summary>
    /// <returns>The sanitized text, or <see langword="null"/> if the data is neither or the text is empty.</returns>
    public static string? DecodeText(ReadOnlySpan<byte> data)
    {
        string? text = null;
        if (data.Length == 32)
        {
            int length = data.IndexOf((byte)0);
            ReadOnlySpan<byte> bytes = length < 0 ? data : data[..length];
            if (bytes.Length > 0 && !data[bytes.Length..].ContainsAnyExcept((byte)0))
            {
                text = Encoding.UTF8.GetString(bytes);
            }
        }
        else if (McpAbiCodec.TryDecode(StringOutput, data, out object?[]? values, out _))
        {
            text = values[0] as string;
        }

        return text is null ? null : Sanitize(text);
    }

    /// <summary>
    /// Makes untrusted contract text safe to show (see <see cref="McpText"/>): no control, format, private-use or unassigned
    /// characters, no variation selectors, trimmed, at most <see cref="MaxTextLength"/> characters.
    /// </summary>
    /// <returns>The sanitized text, or <see langword="null"/> if nothing visible remains.</returns>
    public static string? Sanitize(string text)
    {
        // Trimmed before the cut, so leading blanks (such as control characters turned into spaces) take no room.
        string result = McpText.Sanitize(McpText.Sanitize(text, text.Length).Trim(), MaxTextLength).TrimEnd();
        return result.Length == 0 ? null : result;
    }

    /// <summary>Executes a bounded read-only call and classifies the outcome.</summary>
    /// <returns>The return data, or <see langword="null"/> on failure; <paramref name="transient"/> is set when the failure may go away on retry.</returns>
    internal static byte[]? Call(IEthRpcModule eth, Address to, byte[] input, BlockParameter block, ulong gas, ref bool transient)
    {
        LegacyTransactionForRpc transaction = new() { To = to, Input = input, Gas = gas };
        using ResultWrapper<HexBytes> result = eth.eth_call(transaction, block);
        if (result.Result.ResultType == ResultType.Success)
        {
            return result.Data.Bytes.ToArray();
        }

        // Reverts and EVM errors (-32000, such as out of gas) are the contract's behaviour; anything else may be temporary.
        if (result.IsTemporary || result.ErrorCode is not (ErrorCodes.ExecutionReverted or ErrorCodes.Default))
        {
            transient = true;
        }

        return null;
    }

    private static string? ReadText(IEthRpcModule eth, Address token, BlockParameter block, byte[] selector, ref bool transient) =>
        Call(eth, token, selector, block, MetadataCallGas, ref transient) is { } data ? DecodeText(data) : null;

    private static byte? ReadDecimals(IEthRpcModule eth, Address token, BlockParameter block, ref bool transient)
    {
        byte[]? data = Call(eth, token, DecimalsSelector, block, MetadataCallGas, ref transient);
        if (data is not { Length: >= 32 } || data.AsSpan(0, 31).ContainsAnyExcept((byte)0))
        {
            return null;
        }

        return data[31];
    }

    private void Add((ulong, AddressAsKey) key, McpTokenInfo info)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out LinkedListNode<CacheEntry>? existing))
            {
                _order.Remove(existing);
            }

            _cache[key] = _order.AddFirst(new CacheEntry(key, info));
            while (_cache.Count > CacheCapacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _cache.Remove(last.Value.Key);
            }
        }
    }

    private sealed record CacheEntry((ulong ChainId, AddressAsKey Address) Key, McpTokenInfo Info);
}
